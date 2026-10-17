using EarTrumpet.DataModel.Audio;
using EarTrumpet.DataModel.WindowsAudio;
using EarTrumpet.Extensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Timers;
using System.Windows.Threading;

namespace EarTrumpet.UI.ViewModels
{
    public class DeviceCollectionViewModel : BindableBase
    {
        private static readonly string DefaultDeviceChangedProperty = "DefaultDeviceChangedProperty";

        public event EventHandler<DeviceViewModel> DefaultChanged;
        public event Action TrayPropertyChanged;

        private readonly ObservableCollection<DeviceViewModel> _rawDevices = new ObservableCollection<DeviceViewModel>();

        public ObservableCollection<DeviceViewModel> AllDevices { get; private set; } = new ObservableCollection<DeviceViewModel>();
        public ObservableCollection<DeviceViewModel> AllDevicesWithHidden => _rawDevices;
        public IEnumerable<DeviceViewModel> HiddenDevices => _rawDevices.Where(d => _settings.IsDeviceHidden(d.Id));
        public DeviceViewModel Default { get; private set; }

        private readonly IAudioDeviceManager _deviceManager;
        private readonly Timer _peakMeterTimer;
        private readonly AppSettings _settings;
        private readonly Dispatcher _currentDispatcher = Dispatcher.CurrentDispatcher;
        private bool _isFlyoutVisible;
        private bool _isFullWindowVisible;

        public DeviceCollectionViewModel(IAudioDeviceManager deviceManager, AppSettings settings)
        {
            _settings = settings;
            _settings.HiddenDevicesChanged += OnHiddenDevicesChanged;
            _deviceManager = deviceManager;
            _deviceManager.DefaultChanged += OnDefaultChanged;
            _deviceManager.Devices.CollectionChanged += OnCollectionChanged;
            OnCollectionChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

            _peakMeterTimer = new Timer(1000 / 30); // 30 fps
            _peakMeterTimer.AutoReset = true;
            _peakMeterTimer.Elapsed += PeakMeterTimer_Elapsed;
        }

        public bool IsDeviceHidden(string id) => _settings.IsDeviceHidden(id);

        public void HideDevice(string id)
        {
            _settings.SetDeviceHidden(id, true);
        }

        public void UnhideDevice(string id)
        {
            _settings.SetDeviceHidden(id, false);
        }

        public void UnhideAllDevices()
        {
            var hidden = _settings.HiddenDevices;
            foreach (var id in hidden)
            {
                _settings.SetDeviceHidden(id, false);
            }
        }

        private void OnHiddenDevicesChanged(object sender, EventArgs e)
        {
            _currentDispatcher.BeginInvoke((Action)(() =>
            {
                foreach (var device in _rawDevices)
                {
                    bool shouldBeHidden = _settings.IsDeviceHidden(device.Id);
                    bool isInAllDevices = AllDevices.Contains(device);

                    if (shouldBeHidden && isInAllDevices)
                    {
                        AllDevices.Remove(device);
                    }
                    else if (!shouldBeHidden && !isInAllDevices)
                    {
                        AllDevices.AddSorted(device, DeviceViewModel.CompareByDisplayName);
                    }

                    device.RaiseHiddenChanged();
                }
            }));
        }

        private void OnDefaultChanged(object sender, IAudioDevice newDevice)
        {
            if (newDevice == null)
            {
                SetDefault(null);
            }
            else
            {
                var device = _rawDevices.FirstOrDefault(d => d.Id == newDevice.Id);
                if (device == null)
                {
                    AddDevice(newDevice);
                    device = _rawDevices.FirstOrDefault(d => d.Id == newDevice.Id);
                }
                SetDefault(device);
            }
        }

        private void SetDefault(DeviceViewModel device)
        {
            if (Default != null)
            {
                Default.PropertyChanged -= OnDefaultDevicePropertyChanged;
            }

            Default = device;
            DefaultChanged?.Invoke(this, Default);

            if (Default != null)
            {
                Default.PropertyChanged += OnDefaultDevicePropertyChanged;
            }

            // Let clients know that even though no properties changed, the underlying object changed.
            OnDefaultDevicePropertyChanged(this, new PropertyChangedEventArgs(DefaultDeviceChangedProperty));
        }

        private void OnDefaultDevicePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == DefaultDeviceChangedProperty ||
                e.PropertyName == nameof(Default.Volume) ||
                e.PropertyName == nameof(Default.IsMuted) ||
                e.PropertyName == nameof(Default.DisplayName))
            {
                TrayPropertyChanged?.Invoke();
            }
        }

        protected virtual void AddDevice(IAudioDevice device)
        {
            var newDevice = new DeviceViewModel(this, _deviceManager, device);
            _rawDevices.AddSorted(newDevice, DeviceViewModel.CompareByDisplayName);

            if (!_settings.IsDeviceHidden(newDevice.Id))
            {
                AllDevices.AddSorted(newDevice, DeviceViewModel.CompareByDisplayName);
            }
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    var added = ((IAudioDevice)e.NewItems[0]);
                    var rawExistingAdded = _rawDevices.FirstOrDefault(d => d.Id == added.Id);
                    if (rawExistingAdded == null)
                    {
                        AddDevice(added);
                    }
                    break;

                case NotifyCollectionChangedAction.Remove:
                    var removed = ((IAudioDevice)e.OldItems[0]).Id;
                    var rawExisting = _rawDevices.FirstOrDefault(d => d.Id == removed);
                    if (rawExisting != null)
                    {
                        _rawDevices.Remove(rawExisting);
                        AllDevices.Remove(rawExisting);
                    }
                    break;

                case NotifyCollectionChangedAction.Reset:
                    _rawDevices.Clear();
                    AllDevices.Clear();
                    foreach (var device in _deviceManager.Devices)
                    {
                        AddDevice(device);
                    }
                    break;

                default:
                    throw new NotImplementedException();
            }
        }

        private void PeakMeterTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _deviceManager.UpdatePeakValues();

            _currentDispatcher.BeginInvoke((Action)(() =>
            {
                foreach (var device in AllDevices)
                {
                    device.UpdatePeakValueForeground();
                }
            }));
        }

        public void MoveAppToDevice(IAppItemViewModel app, DeviceViewModel dev)
        {
            // Collect all matching apps on all devices.
            var apps = new List<IAppItemViewModel>();
            apps.Add(app);

            foreach (var device in _rawDevices)
            {
                foreach (var deviceApp in device.Apps)
                {
                    if (deviceApp.DoesGroupWith(app))
                    {
                        if (!apps.Contains(deviceApp))
                        {
                            apps.Add(deviceApp);
                            break;
                        }
                    }
                }
            }

            foreach (var foundApp in apps)
            {
                MoveAppToDeviceInternal(foundApp, dev);
            }

            // Collect and move any hidden/moved sessions.
            ((IAudioDeviceManagerWindowsAudio)_deviceManager).MoveHiddenAppsToDevice(app.AppId, dev?.Id);
        }

        private void MoveAppToDeviceInternal(IAppItemViewModel app, DeviceViewModel device)
        {
            var searchId = device?.Id;
            if (device == null)
            {
                searchId = _deviceManager.Default.Id;
            }

            try
            {
                DeviceViewModel oldDevice = _rawDevices.First(d => d.Apps.Contains(app));
                DeviceViewModel newDevice = _rawDevices.First(d => searchId == d.Id);

                bool isLogicallyMovingDevices = (oldDevice != newDevice);

                var tempApp = new TemporaryAppItemViewModel(this, _deviceManager, app);

                app.MoveToDevice(device?.Id, hide: isLogicallyMovingDevices);

                // Update the UI if the device logically changed places.
                if (isLogicallyMovingDevices)
                {
                    oldDevice.AppLeavingFromThisDevice(app);
                    newDevice.AppMovingToThisDevice(tempApp);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"DeviceCollectionViewModel MoveAppToDeviceInternal Failed: {ex}");
            }
        }

        private void StartOrStopPeakTimer()
        {
            _peakMeterTimer.Enabled = _isFlyoutVisible || _isFullWindowVisible;
        }

        public void OnTrayFlyoutShown()
        {
            _isFlyoutVisible = true;
            StartOrStopPeakTimer();
        }

        public void OnTrayFlyoutHidden()
        {
            _isFlyoutVisible = false;
            StartOrStopPeakTimer();
        }

        public void OnFullWindowClosed()
        {
            _isFullWindowVisible = false;
            StartOrStopPeakTimer();
        }

        public void OnFullWindowOpened()
        {
            _isFullWindowVisible = true;
            StartOrStopPeakTimer();
        }

        public string GetTrayToolTip()
        {
            if (Default != null)
            {
                var stateText = Default.IsMuted ? Properties.Resources.MutedText : $"{Default.Volume}%";
                var prefixText = $"EarTrumpet: {stateText} - ";
                var deviceName = $"{Default.DeviceDescription} ({Default.EnumeratorName})";

                // Remote Audio devices may not contain an enumerator name or description.

                if (string.IsNullOrWhiteSpace(Default.EnumeratorName))
                {
                    deviceName = Default.DeviceDescription;
                }

                if (string.IsNullOrWhiteSpace(Default.DeviceDescription) && string.IsNullOrWhiteSpace(Default.EnumeratorName))
                {
                    deviceName = Default.DisplayName;
                }

                // Device name could be null in transient error cases
                if (deviceName == null)
                {
                    deviceName = "";
                }

                // API Limitation: "less than 64 chars" for the tooltip.
                deviceName = deviceName.Substring(0, Math.Min(63 - prefixText.Length, deviceName.Length));
                return prefixText + deviceName;
            }
            else
            {
                return Properties.Resources.NoDeviceTrayText;
            }
        }
    }
}