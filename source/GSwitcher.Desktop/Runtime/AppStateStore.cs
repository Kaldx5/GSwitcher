using System;
using System.IO;
using System.Runtime.Serialization.Json;
using GSwitcher.Models;

namespace GSwitcher.Services
{
    public static class AppStateStore
    {
        private static readonly object SyncRoot = new object();
        private static AppState _state;

        public static AppState Load()
        {
            lock (SyncRoot)
            {
                return AppState.Normalize(_state == null ? null : _state.Clone());
            }
        }

        public static void Save(AppState state)
        {
            lock (SyncRoot)
            {
                _state = AppState.Normalize(state).Clone();
            }
        }
    }
}
