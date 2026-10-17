using EarTrumpet.UI.Helpers;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace EarTrumpet.UI.ViewModels
{
    public class DeviceSettingItemViewModel : BindableBase
    {
        private readonly DeviceViewModel _device;
        private readonly DeviceCollectionViewModel _collection;
        private readonly AppSettings _settings;

        public string Id => _device.Id;
        public string DisplayName => _device.DisplayName;
        public bool IsDefault => _collection.Default?.Id == _device.Id;
        public bool IsHidden => _settings.IsDeviceHidden(_device.Id);
        public string ActionButtonText => IsHidden ? Properties.Resources.UnhideDeviceText : Properties.Resources.HideDeviceText;
        public string StatusText => IsHidden ? Properties.Resources.HiddenDeviceBadgeText : (IsDefault ? Properties.Resources.DefaultDeviceText : string.Empty);

        public ICommand ToggleHiddenCommand { get; }

        public DeviceSettingItemViewModel(DeviceViewModel device, DeviceCollectionViewModel collection, AppSettings settings)
        {
            _device = device;
            _collection = collection;
            _settings = settings;

            ToggleHiddenCommand = new RelayCommand(() =>
            {
                if (IsHidden)
                {
                    _collection.UnhideDevice(_device.Id);
                }
                else
                {
                    _collection.HideDevice(_device.Id);
                }
                UpdateState();
            });
        }

        public void UpdateState()
        {
            RaisePropertyChanged(nameof(IsDefault));
            RaisePropertyChanged(nameof(IsHidden));
            RaisePropertyChanged(nameof(ActionButtonText));
            RaisePropertyChanged(nameof(StatusText));
        }
    }

    public class EarTrumpetDevicesSettingsPageViewModel : SettingsPageViewModel
    {
        private readonly DeviceCollectionViewModel _collection;
        private readonly AppSettings _settings;

        public ObservableCollection<DeviceSettingItemViewModel> Devices { get; }
        public ICommand UnhideAllCommand { get; }
        public bool HasHiddenDevices => _collection.HiddenDevices.Any();

        public EarTrumpetDevicesSettingsPageViewModel(DeviceCollectionViewModel collection, AppSettings settings) : base(null)
        {
            _collection = collection;
            _settings = settings;

            Title = Properties.Resources.DevicesSettingsPageText;
            Glyph = "\xE767";

            Devices = new ObservableCollection<DeviceSettingItemViewModel>();
            UnhideAllCommand = new RelayCommand(() =>
            {
                _collection.UnhideAllDevices();
                RefreshDevices();
            });

            _settings.HiddenDevicesChanged += OnHiddenDevicesChanged;
            _collection.DefaultChanged += OnDefaultChanged;
            _collection.AllDevicesWithHidden.CollectionChanged += OnDevicesCollectionChanged;

            RefreshDevices();
        }

        private void OnHiddenDevicesChanged(object sender, System.EventArgs e)
        {
            RefreshDevices();
        }

        private void OnDefaultChanged(object sender, DeviceViewModel e)
        {
            foreach (var item in Devices)
            {
                item.UpdateState();
            }
        }

        private void OnDevicesCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RefreshDevices();
        }

        private void RefreshDevices()
        {
            Devices.Clear();
            foreach (var device in _collection.AllDevicesWithHidden.OrderBy(d => d.DisplayName))
            {
                Devices.Add(new DeviceSettingItemViewModel(device, _collection, _settings));
            }
            RaisePropertyChanged(nameof(HasHiddenDevices));
        }
    }
}
