using System.ComponentModel;
using System.Runtime.CompilerServices;
using NinOne.Domain.Devices;

namespace NinOne.App.ViewModels
{
    public sealed class RelayRow : INotifyPropertyChanged
    {
        private bool _state;

        public RelayRow(RelayDefinition definition)
        {
            Definition = definition;
        }

        public RelayDefinition Definition { get; }
        public string Id { get { return Definition.Id; } }
        public string Address { get { return Definition.PlcAddress; } }
        public string Name { get { return Definition.DisplayName; } }
        public bool RequiresConfirmation { get { return Definition.RequiresConfirmation; } }
        public bool State
        {
            get { return _state; }
            set
            {
                if (_state == value) return;
                _state = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
