using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Catches the UI elements the features need at the moment the game creates them - no timer, no tree walks: the game's own Loaded / Unloaded events
    /// (Noesis EventManager class handlers for Canvas and TextBlock) tell when an element appears or goes. The map icons' canvases ("UnitIcon", for
    /// ContactIntel) and the text blocks bound to a weapon entry (for WeaponStatusColors) are kept in lists. Elements that were already loaded when the handlers
    /// were registered are found by one walk over the tree, once.
    /// </summary>
    static class UiScan
    {
        public static Action<string> Trace = s => { };
        public static readonly List<Canvas> Icons = new List<Canvas>();      // the map icons' canvases ("UnitIcon")
        public static readonly List<TextBlock> Texts = new List<TextBlock>(); // text blocks bound to a weapon entry
        public static bool Failed;       // the handlers could not be registered
        static bool _started;
        static readonly ConditionalWeakTable<object, Canvas> IconByContext = new ConditionalWeakTable<object, Canvas>();
        static readonly Stack<DependencyObject> Stack = new Stack<DependencyObject>();

        /// <summary>The canvas of the map icon whose view model this is (null: not loaded yet).</summary>
        public static Canvas IconOf(object viewModel)
        {
            Canvas cv;
            return viewModel != null && IconByContext.TryGetValue(viewModel, out cv) ? cv : null;
        }

        public static bool Started { get { return _started; } }

        /// <summary>Once, when the game's main view exists: registers the handlers and takes what is already loaded.</summary>
        public static void Start(FrameworkElement root)
        {
            if (_started || root == null) return;
            _started = true;
            try
            {
                EventManager.RegisterClassHandler(typeof(Canvas), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnCanvasLoaded));
                EventManager.RegisterClassHandler(typeof(Canvas), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnCanvasUnloaded));
                EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTextLoaded));
                EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnTextUnloaded));
            }
            catch (Exception e) { Failed = true; Trace("UiScan: handlers not registered: " + e.Message); return; }
            try { Walk(root); }
            catch (Exception e) { Trace("UiScan: " + e.Message); }
        }

        static void OnCanvasLoaded(object sender, RoutedEventArgs e)
        {
            try { var cv = sender as Canvas; if (cv != null && cv.Name == "UnitIcon") AddIcon(cv); }
            catch (Exception ex) { Trace("UiScan.Canvas: " + ex.Message); }
        }

        static void OnCanvasUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var cv = sender as Canvas;
                if (cv == null || !Icons.Remove(cv)) return;
                cv.DataContextChanged -= OnContextChanged;
                var dc = cv.DataContext;
                Canvas mapped;
                if (dc != null && IconByContext.TryGetValue(dc, out mapped) && ReferenceEquals(mapped, cv)) IconByContext.Remove(dc);
            }
            catch (Exception ex) { Trace("UiScan.CanvasUnloaded: " + ex.Message); }
        }

        static void OnTextLoaded(object sender, RoutedEventArgs e)
        {
            try { var tb = sender as TextBlock; if (tb != null) AddText(tb); }
            catch (Exception ex) { Trace("UiScan.Text: " + ex.Message); }
        }

        static void OnTextUnloaded(object sender, RoutedEventArgs e)
        {
            try { var tb = sender as TextBlock; if (tb != null && Texts.Remove(tb)) WeaponStatusColors.Detach(tb); }
            catch (Exception ex) { Trace("UiScan.TextUnloaded: " + ex.Message); }
        }

        static void AddIcon(Canvas cv)
        {
            if (Icons.Contains(cv)) return;
            Icons.Add(cv);
            cv.DataContextChanged -= OnContextChanged;
            cv.DataContextChanged += OnContextChanged;
            Map(cv);
            ContactIntel.IconLoaded(cv);
        }

        static void AddText(TextBlock tb)
        {
            if (Texts.Contains(tb) || !WeaponStatusColors.Candidate(tb)) return;
            Texts.Add(tb);
            WeaponStatusColors.Attach(tb);
        }

        static void OnContextChanged(object sender, DependencyPropertyChangedEventArgs a)
        {
            try
            {
                var cv = sender as Canvas;
                if (cv == null) return;
                var old = a.OldValue;
                Canvas mapped;
                if (old != null && IconByContext.TryGetValue(old, out mapped) && ReferenceEquals(mapped, cv)) IconByContext.Remove(old);
                Map(cv);
                ContactIntel.IconLoaded(cv);
            }
            catch (Exception ex) { Trace("UiScan.ContextChanged: " + ex.Message); }
        }

        static void Map(Canvas cv)
        {
            var dc = cv.DataContext;
            if (dc == null) return;
            IconByContext.Remove(dc);
            IconByContext.Add(dc, cv);
        }

        /// <summary>The one walk, when the handlers are registered: what is already on screen.</summary>
        static void Walk(FrameworkElement root)
        {
            Stack.Clear();
            Stack.Push(root);
            int budget = 60000;
            while (Stack.Count > 0 && budget-- > 0)
            {
                var e = Stack.Pop();
                var cv = e as Canvas;
                if (cv != null && cv.Name == "UnitIcon") AddIcon(cv);
                var tb = e as TextBlock;
                if (tb != null) AddText(tb);
                int n;
                try { n = VisualTreeHelper.GetChildrenCount(e); } catch { n = 0; }
                for (int k = 0; k < n; k++)
                {
                    DependencyObject c = null;
                    try { c = VisualTreeHelper.GetChild(e, k); } catch { }
                    if (c != null) Stack.Push(c);
                }
            }
            Stack.Clear();
        }
    }
}
