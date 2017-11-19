using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AVFoundation;
using CoreFoundation;
using CoreMedia;
using Foundation;
using MediaPlayer;
using Plugin.MediaManager.Abstractions;
using Plugin.MediaManager.Abstractions.Enums;
using Plugin.MediaManager.Abstractions.EventArguments;

namespace Plugin.MediaManager
{
    public class AudioPlayerImplementation : NSObject, IAudioPlayer
    {
        private readonly NSString _statusObservationKey = new NSString(Constants.StatusObservationKey);
        private readonly NSString _rateObservationKey = new NSString(Constants.RateObservationKey);
        private readonly NSString _loadedTimeRangesObservationKey = new NSString(Constants.LoadedTimeRangesObservationKey);
        private readonly NSString _playbackLikelyToKeepUpKey = new NSString(Constants.PlaybackLikelyToKeepUpKey);
        private readonly NSString _playbackBufferFullKey = new NSString(Constants.PlaybackBufferFullKey);

        private readonly AVQueuePlayer _player = new AVQueuePlayer();
        private readonly List<AVPlayerItem> _playerItems = new List<AVPlayerItem>();
        private readonly IDictionary<Guid, AVPlayerItem> _playerItemByMediaFileIdDict = new Dictionary<Guid, AVPlayerItem>();

        private readonly IMediaQueue _mediaQueue;
        private readonly IVolumeManager _volumeManager;
        private readonly IVersionHelper _versionHelper;

        private IMediaFile _currentMediaFile;
        private MediaPlayerStatus _status;

        private bool _justFinishedSeeking;

        public AudioPlayerImplementation(IMediaQueue mediaQueue, IVolumeManager volumeManager)
        {
            _mediaQueue = mediaQueue;
            _volumeManager = volumeManager;
            _versionHelper = new VersionHelper();

            _mediaQueue.CollectionChanged += MediaQueueOnCollectionChanged;

            InitializePlayer();

            _status = MediaPlayerStatus.Stopped;

            _volumeManager.Muted = _player.Muted;
            _volumeManager.CurrentVolume = (int)_player.Volume * 100;
            _volumeManager.MaxVolume = 100;
            _volumeManager.VolumeChanged += VolumeManagerOnVolumeChanged;
        }

        public event StatusChangedEventHandler StatusChanged;
        public event PlayingChangedEventHandler PlayingChanged;
        public event BufferingChangedEventHandler BufferingChanged;
        public event MediaFinishedEventHandler MediaFinished;
        public event MediaFailedEventHandler MediaFailed;

        public Dictionary<string, string> RequestHeaders { get; set; }

        public float Rate
        {
            get => _player?.Rate ?? 0.0f;
            set
            {
                if (_player != null)
                    _player.Rate = value;
            }
        }

        public TimeSpan Position
        {
            get
            {
                if (CurrentItem == null)
                    return TimeSpan.Zero;
                return TimeSpan.FromSeconds(CurrentItem.CurrentTime.Seconds);
            }
        }

        public TimeSpan Duration
        {
            get
            {
                if (CurrentItem == null || CurrentItem.Duration.IsIndefinite ||
                    CurrentItem.Duration.IsInvalid)
                    return TimeSpan.Zero;
                return TimeSpan.FromSeconds(CurrentItem.Duration.Seconds);
            }
        }

        public TimeSpan Buffered
        {
            get
            {
                var buffered = TimeSpan.Zero;

                var currentItem = CurrentItem;

                var loadedTimeRanges = currentItem?.LoadedTimeRanges;

                if (currentItem != null && loadedTimeRanges.Any())
                {
                    var loadedSegments = loadedTimeRanges
                        .Select(timeRange =>
                        {
                            var timeRangeValue = timeRange.CMTimeRangeValue;

                            var startSeconds = timeRangeValue.Start.Seconds;
                            var durationSeconds = timeRangeValue.Duration.Seconds;

                            return startSeconds + durationSeconds;
                        });

                    var loadedSeconds = loadedSegments.Max();

                    buffered = TimeSpan.FromSeconds(loadedSeconds);
                }

                Console.WriteLine("Buffered size: " + buffered);

                return buffered;
            }
        }

        public MediaPlayerStatus Status
        {
            get => _status;
            private set
            {
                var statusChanged = _status != value;
                _status = value;

                if (statusChanged)
                {
                    StatusChanged?.Invoke(this, new StatusChangedEventArgs(_status));
                }
            }
        }

        private AVPlayerItem CurrentItem => _player.CurrentItem;

        public override async void ObserveValue(NSString keyPath, NSObject ofObject, NSDictionary change, IntPtr context)
        {
            Console.WriteLine("Observer triggered for {0}", keyPath);

            switch (keyPath)
            {
                case Constants.StatusObservationKey:
                    HandlePlaybackStatusChange();
                    break;
                case Constants.LoadedTimeRangesObservationKey:
                    HandleLoadedTimeRangesChange();
                    break;
                case Constants.RateObservationKey:
                    HandlePlaybackRateChange();
                    break;
                case Constants.PlaybackLikelyToKeepUpKey:
                case Constants.PlaybackBufferFullKey:
                    if (_justFinishedSeeking)
                    {
                        if (Status == MediaPlayerStatus.Paused)
                        {
                            await Play();
                        }
                        _justFinishedSeeking = false;
                    }
                    break;
            }
        }

        public async Task Play(IMediaFile mediaFile = null)
        {
            if (mediaFile == null && _player.CurrentItem == null)
            {
                Status = MediaPlayerStatus.Failed;
                return;
            }

            if (mediaFile == null || mediaFile.Equals(_currentMediaFile) && Status == MediaPlayerStatus.Paused)
            {
                _player.Play();
                Status = MediaPlayerStatus.Playing;
                return;
            }

            AVPlayerItem playerItemToPlay = null;
            if (_playerItemByMediaFileIdDict.ContainsKey(mediaFile.Id))
            {
                playerItemToPlay = _playerItemByMediaFileIdDict[mediaFile.Id];
            }
            else
            {
                var url = MediaFileUrlHelper.GetUrlFor(mediaFile);
                playerItemToPlay = GetPlayerItem(url);
            }

            if (playerItemToPlay == null)
            {
                Status = MediaPlayerStatus.Failed;
                return;
            }

            try
            {
                CurrentItem?.RemoveObserver(this, _statusObservationKey);

                var indexOfCurrentItem = _playerItems.IndexOf(CurrentItem);
                var indexOfItemToPlay = _playerItems.IndexOf(playerItemToPlay);
                // the item to play is the next one on the list
                if (indexOfCurrentItem >= 0 && indexOfItemToPlay >= 0 && indexOfCurrentItem + 1 == indexOfItemToPlay)
                {
                    _player.AdvanceToNextItem();
                }
                else
                {
                    // Unfortunately, there's no other way of playing previous episode.
                    // The current list needs to be cleared and recrated, starting from the episode to play
                    // This implementation is inspired by the following StackOverflow thread: https://stackoverflow.com/questions/12176699/skip-to-previous-avplayeritem-on-avqueueplayer-play-selected-item-from-queue
                    _player.RemoveAllItems();
                    // the item to play is not in the current list
                    if (indexOfItemToPlay < 0)
                    {
                        _playerItemByMediaFileIdDict.Clear();
                        _playerItems.Clear();
                        _player.ReplaceCurrentItemWithPlayerItem(playerItemToPlay);

                        _playerItems.Add(playerItemToPlay);
                        _playerItemByMediaFileIdDict.Add(mediaFile.Id, playerItemToPlay);
                    }
                    else
                    {
                        for (int i = indexOfItemToPlay; i < _playerItems.Count; i++)
                        {
                            if (_player.CanInsert(_playerItems[i], null))
                            {
                                _player.InsertItem(_playerItems[i], null);
                            }
                        }
                    }
                }

                _currentMediaFile = mediaFile;
                Status = MediaPlayerStatus.Buffering;

                // ReSharper disable once PossibleNullReferenceException
                CurrentItem.AddObserver(this, _loadedTimeRangesObservationKey, NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _loadedTimeRangesObservationKey.Handle);
                CurrentItem.AddObserver(this, _statusObservationKey, NSKeyValueObservingOptions.New | NSKeyValueObservingOptions.Initial, _statusObservationKey.Handle);
                CurrentItem.AddObserver(this, _playbackLikelyToKeepUpKey, NSKeyValueObservingOptions.New | NSKeyValueObservingOptions.Initial, _playbackLikelyToKeepUpKey.Handle);
                CurrentItem.AddObserver(this, _playbackBufferFullKey, NSKeyValueObservingOptions.New | NSKeyValueObservingOptions.Initial, _playbackBufferFullKey.Handle);

                NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, HandlePlaybackFinished, CurrentItem);

                _player.Play();

                UpdateRemoteControlButtonsVisibility();
            }
            catch (Exception ex)
            {
                HandleMediaPlaybackFailure(ex);
                Status = MediaPlayerStatus.Stopped;

                //unable to start playback log error
                Console.WriteLine("Unable to start playback: " + ex);
            }

            await Task.CompletedTask;
        }

        public async Task Stop()
        {
            if (CurrentItem == null)
            {
                return;
            }

            await Task.Run(() =>
            {
                _player.Pause();
                CurrentItem.Seek(CMTime.FromSeconds(0d, 1));

                Status = MediaPlayerStatus.Stopped;
            });
        }

        public async Task Pause()
        {
            if (CurrentItem == null)
            {
                return;
            }

            await Task.Run(() =>
            {
                _player.Pause();
                Status = MediaPlayerStatus.Paused;
            });
        }

        public async Task Seek(TimeSpan position)
        {
            await Pause();
            CurrentItem?.Seek(CMTime.FromSeconds(position.TotalSeconds, 1), HandlePlaybackSeekCompleted);

            await Task.CompletedTask;
        }

        private void InitializePlayer()
        {
            if (_versionHelper.SupportsAutomaticWaitPlayerProperty)
            {
                _player.AutomaticallyWaitsToMinimizeStalling = false;
            }

#if __IOS__ || __TVOS__
            var avSession = AVAudioSession.SharedInstance();

            // By setting the Audio Session category to AVAudioSessionCategorPlayback, audio will continue to play when the silent switch is enabled, or when the screen is locked.
            avSession.SetCategory(AVAudioSessionCategory.Playback);
            avSession.SetActive(true, out var activationError);
            if (activationError != null)
            {
                Console.WriteLine("Could not activate audio session {0}", activationError.LocalizedDescription);
            }
#endif
            _player.AddObserver(this, _rateObservationKey, NSKeyValueObservingOptions.New | NSKeyValueObservingOptions.Initial, _rateObservationKey.Handle);
            // CMTime(1,2) means that the event will be fired 2 times a second
            _player.AddPeriodicTimeObserver(new CMTime(1, 2), DispatchQueue.MainQueue, HandleTimeChange);
        }

        private async void MediaQueueOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    // TODO figure out when we can actually manipulate the queue (without this delay Mono throws exceptions and crashes the app)
                    await Task.Delay(10000);
                    HandleMediaQueueAddAction(e);
                    break;
                    //case NotifyCollectionChangedAction.Move:
                    //    // The reality is that this scenario is never going to happen. Even when we re-order or shuffle, the list is being regenerated (Reset)
                    //    break;
                    //case NotifyCollectionChangedAction.Remove:
                    //    HandleMediaQueueRemoveAction(e);
                    //    break;
                    //case NotifyCollectionChangedAction.Replace:
                    //    await HandleMediaQueueReplaceAction(e);
                    //    break;
                    //case NotifyCollectionChangedAction.Reset:
                    //    await HandleMediaQueueResetAction(sender as IEnumerable<IMediaFile>);
                    //    break;
            }

            Debug.WriteLine($"There's {_player.Items.Length} items in the playback queue");

            if (_player.CurrentItem == null)
            {
                return;
            }

            UpdateRemoteControlButtonsVisibility();
        }

        private void VolumeManagerOnVolumeChanged(object sender, VolumeChangedEventArgs volumeChangedEventArgs)
        {
            _player.Volume = (float)volumeChangedEventArgs.NewVolume / 100;
            _player.Muted = volumeChangedEventArgs.Muted;
        }

        private AVPlayerItem GetPlayerItem(NSUrl url)
        {
            if (url == null)
            {
                return null;
            }

            AVAsset asset;

            if (RequestHeaders?.Any() ?? false)
            {
                var options = MediaFileUrlHelper.GetOptionsWithHeaders(RequestHeaders);
                asset = AVUrlAsset.Create(url, options);
            }
            else
            {
                asset = AVAsset.FromUrl(url);
            }

            var playerItem = AVPlayerItem.FromAsset(asset);
            return playerItem;
        }

        private void HandleTimeChange(CMTime time)
        {
            if (CurrentItem.Duration.IsInvalid || CurrentItem.Duration.IsIndefinite || double.IsNaN(CurrentItem.Duration.Seconds))
            {
                PlayingChanged?.Invoke(this, new PlayingChangedEventArgs(0, Position, Duration));
            }
            else
            {
                // Prevent potential division by 0
                if (CurrentItem.Duration.Seconds <= 0)
                {
                    return;
                }

                var totalDuration = TimeSpan.FromSeconds(CurrentItem.Duration.Seconds);
                var totalProgress = Position.TotalMilliseconds / totalDuration.TotalMilliseconds;

                Debug.WriteLine($"Total Progress: {Position.Minutes} { Position.Seconds}");

                PlayingChanged?.Invoke(this, new PlayingChangedEventArgs(!double.IsInfinity(totalProgress) ? totalProgress : 0, Position, Duration));
            }
        }

        private void HandlePlaybackStatusChange()
        {
            Console.WriteLine("Status Observed Method {0}", CurrentItem.Status);

            var isBuffering = Status == MediaPlayerStatus.Buffering;
            if (CurrentItem.Status == AVPlayerItemStatus.ReadyToPlay && isBuffering)
            {
                Status = MediaPlayerStatus.Playing;
            }
            else if (CurrentItem.Status == AVPlayerItemStatus.Failed)
            {
                HandleMediaPlaybackFailure();
                Status = MediaPlayerStatus.Stopped;
            }
        }

        private void HandlePlaybackRateChange()
        {
            var stoppedPlaying = Rate == 0.0;
            if (stoppedPlaying && Status == MediaPlayerStatus.Playing)
            {
                //Update the status becuase the system changed the rate.
                Status = MediaPlayerStatus.Paused;
            }
        }

        private void HandleLoadedTimeRangesChange()
        {
            var loadedTimeRanges = CurrentItem.LoadedTimeRanges;
            var hasLoadedAnyTimeRanges = loadedTimeRanges != null && loadedTimeRanges.Length > 0;

            if (hasLoadedAnyTimeRanges)
            {
                var range = loadedTimeRanges[0].CMTimeRangeValue;
                var duration = double.IsNaN(range.Duration.Seconds) ? TimeSpan.Zero : TimeSpan.FromSeconds(range.Duration.Seconds);
                var totalDuration = CurrentItem.Duration;
                var bufferProgress = duration.TotalSeconds / totalDuration.Seconds;

                BufferingChanged?.Invoke(this, new BufferingChangedEventArgs(!double.IsInfinity(bufferProgress) ? bufferProgress : 0, duration));
            }
            else
            {
                BufferingChanged?.Invoke(this, new BufferingChangedEventArgs(0, TimeSpan.Zero));
            }
        }

        private void HandleMediaPlaybackFailure(Exception ex = null)
        {
            string errorMsg;
            Exception exception;

            if (CurrentItem.Error != null)
            {
                errorMsg = CurrentItem.Error.LocalizedDescription;
                exception = new NSErrorException(CurrentItem.Error);
            }
            else
            {
                errorMsg = ex?.Message;
                exception = ex;
            }

            MediaFailed?.Invoke(this, new MediaFailedEventArgs(errorMsg, exception));
        }

        private void HandlePlaybackSeekCompleted(bool seekingCompleted)
        {
            Debug.WriteLine($"Seeking to finished? {seekingCompleted}");

            _justFinishedSeeking = seekingCompleted;
        }

        private void HandlePlaybackFinished(NSNotification notification)
        {
            MediaFinished?.Invoke(this, new MediaFinishedEventArgs(_currentMediaFile));
        }

        private void HandleMediaQueueAddAction(NotifyCollectionChangedEventArgs e)
        {
            if (e?.NewItems == null)
            {
                return;
            }

            var newMediaFiles = new List<IMediaFile>();
            foreach (var newItem in e.NewItems)
            {
                if (newItem is IMediaFile mediaFile)
                {
                    newMediaFiles.Add(mediaFile);
                }
                else if (newItem is IEnumerable<IMediaFile> mediaFiles)
                {
                    newMediaFiles.AddRange(mediaFiles);
                }

                foreach (var newMediaFile in newMediaFiles)
                {
                    if (_playerItemByMediaFileIdDict.ContainsKey(newMediaFile.Id))
                    {
                        continue;
                    }

                    var url = MediaFileUrlHelper.GetUrlFor(newMediaFile);
                    var playerItem = GetPlayerItem(url);

                    if (_player.CanInsert(playerItem, null))
                    {
                        _player.InsertItem(playerItem, null);
                        _playerItems.Add(playerItem);
                        _playerItemByMediaFileIdDict.Add(newMediaFile.Id, playerItem);
                    }
                    else
                    {
                        Debug.WriteLine($"Unable to insert player item {newMediaFile.Metadata?.Title} into the queue");
                    }
                }
            }
        }

        private void UpdateRemoteControlButtonsVisibility()
        {
            var indexOfCurrentPlayerItem = _playerItems.IndexOf(_player.CurrentItem);
            if (indexOfCurrentPlayerItem < 0)
            {
                return;
            }

            var hasNext = _playerItems.Count - 1 - indexOfCurrentPlayerItem > 0;
            var hasPrevious = indexOfCurrentPlayerItem > 0;

            MPRemoteCommandCenter.Shared.NextTrackCommand.Enabled = hasNext;
            MPRemoteCommandCenter.Shared.PreviousTrackCommand.Enabled = hasPrevious;
            MPRemoteCommandCenter.Shared.SkipForwardCommand.Enabled = !hasNext;
            MPRemoteCommandCenter.Shared.SkipBackwardCommand.Enabled = !hasPrevious;
        }
    }
}