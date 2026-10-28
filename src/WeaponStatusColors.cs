using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// [UI] ColorWeaponStatus: the weapon buttons of the selected unit's bottom panel (one per weapon type: its name and the ammo count)
    /// are coloured by the state of the weapon systems behind them (WeaponEntry.WeaponSystems, BaseSystem.Inoperable):
    /// all working - the game's own colour; some inoperable - yellow; all inoperable - red.
    /// The panel is found from MainGameView.Instance: a text block whose DataContext is a WeaponEntry and whose text is the entry's
    /// Label or AmmoAmountText. The blocks are caught when the game creates them (UiScan); a system's Inoperable flag is watched, so the colour changes
    /// the moment the weapon is lost or repaired. Colours are put back (ClearValue) when the weapons work again or the setting is switched off.
    /// </summary>
    static class WeaponStatusColors
    {
        public static Action<string> Trace = s => { };

        static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(240, 208, 74));
        static readonly Brush Red = new SolidColorBrush(Color.FromRgb(236, 70, 56));

        static bool _init, _ok;
        static Type _entryType, _mainViewType;
        static FieldInfo _mainInstance;
        static PropertyInfo _pLabel, _pAmmoText, _pSystems, _pInoperable;
        static PropertyInfo _pValue;
        static bool _on;
        static readonly HashSet<TextBlock> Colored = new HashSet<TextBlock>();
        static readonly Dictionary<object, List<IDisposable>> Subs = new Dictionary<object, List<IDisposable>>();

        public static void Init(Assembly asm)
        {
            if (_init) return;
            _init = true;
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "WeaponEntry" && _entryType == null) _entryType = t;
                    else if (t.Name == "MainGameView" && _mainViewType == null) _mainViewType = t;
                }
                if (_entryType == null || _mainViewType == null) { Trace("WeaponStatusColors: types not found"); return; }
                _mainInstance = _mainViewType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _pLabel = _entryType.GetProperty("Label", all);
                _pAmmoText = _entryType.GetProperty("AmmoAmountText", all);
                _pSystems = _entryType.GetProperty("WeaponSystems", all);
                var tBase = asm.GetType("SeaPower.BaseSystem");
                _pInoperable = tBase == null ? null : tBase.GetProperty("Inoperable", all);
                _ok = _mainInstance != null && _pLabel != null && _pAmmoText != null && _pSystems != null && _pInoperable != null;
                if (!_ok) Trace("WeaponStatusColors: members not found");
            }
            catch (Exception e) { Trace("WeaponStatusColors.Init: " + e.Message); _ok = false; }
        }

        /// <summary>The game's main view (the root of the tree the map lives in); null while it is not there.</summary>
        public static FrameworkElement Root() { return _ok ? _mainInstance.GetValue(null) as FrameworkElement : null; }

        public static bool Ready { get { return _ok; } }

        /// <summary>A text block bound to a weapon entry.</summary>
        public static bool Candidate(TextBlock tb)
        {
            if (!_ok) return false;
            object dc = null;
            try { dc = tb.DataContext; } catch { }
            return dc != null && _entryType.IsInstanceOfType(dc);
        }

        /// <summary>The setting changed: off puts the game's colours back, on colours what is on screen. Called every frame, acts on a change only.</summary>
        public static void SetOn(bool on)
        {
            if (!_ok || on == _on) return;
            _on = on;
            try
            {
                if (!on) ClearAll();
                else foreach (var tb in UiScan.Texts.ToArray()) Paint(tb);
            }
            catch (Exception e) { Trace("WeaponStatusColors.SetOn: " + e.Message); }
        }

        /// <summary>A weapon text block appeared: its entry's systems are watched (their Inoperable flag) and the block is coloured by the state.</summary>
        public static void Attach(TextBlock tb)
        {
            if (!_ok) return;
            object dc = null;
            try { dc = tb.DataContext; } catch { }
            if (dc == null) return;
            if (!Subs.ContainsKey(dc))
            {
                var list = new List<IDisposable>();
                Subs[dc] = list;
                var systems = _pSystems.GetValue(dc, null) as IEnumerable;
                if (systems != null)
                    foreach (var s in systems)
                    {
                        var rp = s == null ? null : _pInoperable.GetValue(s, null) as IObservable<bool>;
                        if (rp == null) continue;
                        try { list.Add(rp.Subscribe(new Watch(dc))); } catch (Exception e) { Trace("WeaponStatusColors.Subscribe: " + e.Message); }
                    }
            }
            if (_on) Paint(tb);
        }

        /// <summary>The text block is gone: its colour is forgotten and, when it was the last of its entry, the entry's subscriptions end.</summary>
        public static void Detach(TextBlock tb)
        {
            Colored.Remove(tb);
            object dc = null;
            try { dc = tb.DataContext; } catch { }
            if (dc == null) return;
            foreach (var other in UiScan.Texts) { try { if (ReferenceEquals(other.DataContext, dc)) return; } catch { } }
            List<IDisposable> list;
            if (Subs.TryGetValue(dc, out list)) { foreach (var d in list) try { d.Dispose(); } catch { } Subs.Remove(dc); }
        }

        static void ClearAll()
        {
            foreach (var tb in Colored) try { tb.ClearValue(TextBlock.ForegroundProperty); } catch { }
            Colored.Clear();
        }

        /// <summary>A system of the entry became inoperable or works again.</summary>
        sealed class Watch : IObserver<bool>
        {
            readonly WeakReference _entry;
            public Watch(object entry) { _entry = new WeakReference(entry); }
            public void OnNext(bool value)
            {
                var entry = _entry.Target;
                if (entry == null || !_on) return;
                try { foreach (var tb in UiScan.Texts.ToArray()) if (ReferenceEquals(tb.DataContext, entry)) Paint(tb); }
                catch (Exception e) { Trace("WeaponStatusColors.Watch: " + e.Message); }
            }
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }

        /// <summary>0 - works (or nothing known), 1 - some systems inoperable, 2 - all of them.</summary>
        static int State(object entry)
        {
            var systems = _pSystems.GetValue(entry, null) as IEnumerable;
            if (systems == null) return 0;
            int n = 0, bad = 0;
            foreach (var s in systems)
            {
                if (s == null) continue;
                n++;
                var rp = _pInoperable.GetValue(s, null);
                if (rp == null) continue;
                if (_pValue == null) _pValue = rp.GetType().GetProperty("Value");
                if (_pValue != null && (bool)_pValue.GetValue(rp, null)) bad++;
            }
            return n == 0 || bad == 0 ? 0 : bad < n ? 1 : 2;
        }

        /// <summary>Colours one text block (the label or the ammo count) by the state of its entry's weapon systems.</summary>
        static void Paint(TextBlock tb)
        {
            object dc = null;
            try { dc = tb.DataContext; } catch { }
            if (dc == null || !_entryType.IsInstanceOfType(dc)) return;
            string text = tb.Text;
            if (string.IsNullOrEmpty(text) || !(text == (string)_pLabel.GetValue(dc, null) || text == (string)_pAmmoText.GetValue(dc, null))) return;
            int st = State(dc);
            Brush want = st == 2 ? Red : st == 1 ? Yellow : null;
            if (want == null)
            {
                if (Colored.Remove(tb)) tb.ClearValue(TextBlock.ForegroundProperty);
            }
            else
            {
                if (!ReferenceEquals(tb.Foreground, want)) tb.Foreground = want;
                Colored.Add(tb);
            }
        }
    }
}
