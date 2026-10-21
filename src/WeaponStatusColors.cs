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
    /// Label or AmmoAmountText. The tree is scanned once a second; colours are put back (ClearValue) when the weapons work again
    /// or the setting is switched off.
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
        static float _next;
        static readonly List<WeakReference> Colored = new List<WeakReference>();

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

        /// <summary>A text block bound to a weapon entry (UiScan collects these in its walk).</summary>
        public static bool Candidate(TextBlock tb)
        {
            if (!_ok) return false;
            object dc = null;
            try { dc = tb.DataContext; } catch { }
            return dc != null && _entryType.IsInstanceOfType(dc);
        }

        /// <summary>Called every frame; works once a second on the text blocks UiScan found (it walks the tree itself, every 2 s).</summary>
        public static void Tick(float now, bool on)
        {
            if (!_ok) return;
            if (!on)
            {
                if (Colored.Count > 0) ClearAll();
                return;
            }
            if (now < _next) return;
            _next = now + 1f;
            try { Apply(); } catch (Exception e) { Trace("WeaponStatusColors.Apply: " + e.Message); }
        }

        static void ClearAll()
        {
            foreach (var w in Colored)
            {
                var tb = w.Target as TextBlock;
                if (tb != null) try { tb.ClearValue(TextBlock.ForegroundProperty); } catch { }
            }
            Colored.Clear();
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

        static void Apply()
        {
            var texts = UiScan.Texts;
            var seen = new HashSet<TextBlock>();
            for (int i = 0; i < texts.Count; i++)
            {
                var tb = texts[i];
                object dc = null;
                try { dc = tb.DataContext; } catch { }
                if (dc == null || !_entryType.IsInstanceOfType(dc)) continue;
                string text = tb.Text;
                if (string.IsNullOrEmpty(text) || !(text == (string)_pLabel.GetValue(dc, null) || text == (string)_pAmmoText.GetValue(dc, null))) continue;
                seen.Add(tb);
                int st = State(dc);
                Brush want = st == 2 ? Red : st == 1 ? Yellow : null;
                if (want == null)
                {
                    if (Tracked(tb)) { tb.ClearValue(TextBlock.ForegroundProperty); Untrack(tb); }
                }
                else
                {
                    if (!ReferenceEquals(tb.Foreground, want)) tb.Foreground = want;
                    if (!Tracked(tb)) Colored.Add(new WeakReference(tb));
                }
            }
            // coloured blocks that are gone (another unit selected) are forgotten
            Colored.RemoveAll(w => { var t = w.Target as TextBlock; return t == null || !seen.Contains(t); });
        }

        static bool Tracked(TextBlock tb)
        {
            foreach (var w in Colored) if (ReferenceEquals(w.Target, tb)) return true;
            return false;
        }

        static void Untrack(TextBlock tb) { Colored.RemoveAll(w => ReferenceEquals(w.Target, tb)); }
    }
}
