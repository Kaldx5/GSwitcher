using System;
using GSwitcher.Models;

namespace GSwitcher.ViewModels
{
    public sealed class MainViewModel
    {
        private readonly Action<string> _commandHandler;

        public MainViewModel(AppState initialState, Action<string> commandHandler)
        {
            State = initialState ?? AppState.CreateDefault();
            _commandHandler = commandHandler;
        }

        public AppState State { get; private set; }

        public void UpdateState(AppState state)
        {
            State = state ?? AppState.CreateDefault();
        }

        public void HandleWebMessage(string message)
        {
            if (_commandHandler != null)
            {
                _commandHandler(message);
            }
        }
    }
}
