using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

// WKSR's logic (MainViewModel*.cs) was written for a WPF window, in the WKSR program that came before the in-game
// menu. These are the few WPF types it names, reduced to what it uses of them. Everything queued on
// the dispatcher runs on the game's main thread, once a frame (Dispatcher.Pump, called by the embedded client).

namespace System.Windows.Media
{
    public struct Color
    {
        public byte R, G, B;

        public static Color FromRgb(byte r, byte g, byte b)
        {
            return new Color { R = r, G = g, B = b };
        }
    }

    public class Brush
    {
        public Color Color;

        public void Freeze()
        {
        }
    }

    public sealed class SolidColorBrush : Brush
    {
        public SolidColorBrush(Color color)
        {
            Color = color;
        }
    }
}

namespace System.Windows.Threading
{
    public enum DispatcherPriority
    {
        ApplicationIdle,
        Background,
        Normal,
    }

    public sealed class Dispatcher
    {
        private static readonly Dispatcher Main = new Dispatcher();
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly List<DispatcherTimer> _timers = new List<DispatcherTimer>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public static Dispatcher CurrentDispatcher { get { return Main; } }

        // Raised for an action that threw, so one bad message cannot stop the rest.
        public static event Action<Exception> Failed;

        private int _mainThread = -1;

        // The thread that pumps: the game's main thread.
        public void Attach()
        {
            _mainThread = global::System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        public bool CheckAccess()
        {
            return _mainThread == global::System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        public DispatcherOperation BeginInvoke(Action action)
        {
            if (action != null) _queue.Enqueue(action);
            return DispatcherOperation.Queued;
        }

        public DispatcherOperation BeginInvoke(Delegate action, params object[] args)
        {
            if (action == null) return DispatcherOperation.Queued;
            var simple = action as Action;
            if (simple != null) _queue.Enqueue(simple);
            else _queue.Enqueue(() => action.DynamicInvoke(args));
            return DispatcherOperation.Queued;
        }

        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate action)
        {
            return BeginInvoke(action);
        }

        internal void Add(DispatcherTimer timer)
        {
            if (!_timers.Contains(timer)) _timers.Add(timer);
        }

        internal void Remove(DispatcherTimer timer)
        {
            _timers.Remove(timer);
        }

        // Runs what was queued since the last frame, then the timers that are due. Main thread only.
        public void Pump()
        {
            Action action;
            for (int budget = 2048; budget > 0 && _queue.TryDequeue(out action); budget--)
            {
                try { action(); }
                catch (Exception e) { Failed?.Invoke(e); }
            }
            long now = _clock.ElapsedMilliseconds;
            for (int i = 0; i < _timers.Count; i++)
            {
                try { _timers[i].Run(now); }
                catch (Exception e) { Failed?.Invoke(e); }
            }
        }
    }

    public sealed class DispatcherOperation
    {
        internal static readonly DispatcherOperation Queued = new DispatcherOperation();
    }

    public sealed class DispatcherTimer
    {
        private long _next = -1;

        public TimeSpan Interval { get; set; }
        public event EventHandler Tick;

        public void Start()
        {
            _next = 0;
            Dispatcher.CurrentDispatcher.Add(this);
        }

        public void Stop()
        {
            Dispatcher.CurrentDispatcher.Remove(this);
        }

        internal void Run(long nowMs)
        {
            if (nowMs < _next) return;
            _next = nowMs + (long)Math.Max(1, Interval.TotalMilliseconds);
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

namespace System.Windows
{
    public sealed class Application
    {
        private static readonly Application Instance = new Application();

        public static Application Current { get { return Instance; } }
        public Threading.Dispatcher Dispatcher { get { return Threading.Dispatcher.CurrentDispatcher; } }
    }

    public static class Clipboard
    {
        public static bool ContainsText()
        {
            return !string.IsNullOrEmpty(UnityEngine.GUIUtility.systemCopyBuffer);
        }

        public static string GetText()
        {
            return UnityEngine.GUIUtility.systemCopyBuffer ?? "";
        }

        public static void SetText(string text)
        {
            UnityEngine.GUIUtility.systemCopyBuffer = text ?? "";
        }
    }
}

namespace System.Windows.Input
{
    public static class CommandManager
    {
#pragma warning disable 67
        public static event EventHandler RequerySuggested;
#pragma warning restore 67

        // The in-game menu asks a command whether it can run each frame it draws its button: nothing to refresh.
        public static void InvalidateRequerySuggested()
        {
        }
    }
}
