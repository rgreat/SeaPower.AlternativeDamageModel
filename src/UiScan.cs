using System;
using System.Collections.Generic;
using Noesis;

namespace AlternativeDamageModel
{
    /// <summary>
    /// One walk over the game's main view tree for the features that look for something in it (the map icons' notes - ContactIntel,
    /// the weapon button colours - WeaponStatusColors). Each VisualTreeHelper call crosses into the native UI, so a walk over thousands
    /// of elements costs a noticeable part of a frame: it was done twice a second (once for each feature); now once per Every seconds,
    /// both features take what they need from the same pass and work on the lists it made (every second, cheap).
    /// </summary>
    static class UiScan
    {
        public static Action<string> Trace = s => { };
        public const float Every = 2f;
        public static readonly List<Canvas> Icons = new List<Canvas>();      // the map icons' canvases ("UnitIcon")
        public static readonly List<TextBlock> Texts = new List<TextBlock>(); // text blocks bound to a weapon entry
        public static int IconWalks;     // walks that looked for the map icons
        public static bool FoundIcons;   // ... and found some at least once
        static float _next;
        static readonly Stack<DependencyObject> Stack = new Stack<DependencyObject>();

        public static void Tick(float now, FrameworkElement root, bool wantIcons, bool wantTexts)
        {
            if (root == null || (!wantIcons && !wantTexts))
            {
                if (Icons.Count > 0 || Texts.Count > 0) { Icons.Clear(); Texts.Clear(); }
                _next = 0f;
                return;
            }
            if (now < _next) return;
            _next = now + Every;
            try { Walk(root, wantIcons, wantTexts); if (wantIcons) { IconWalks++; if (Icons.Count > 0) FoundIcons = true; } }
            catch (Exception e) { Trace("UiScan: " + e.Message); }
        }

        static void Walk(FrameworkElement root, bool wantIcons, bool wantTexts)
        {
            Icons.Clear(); Texts.Clear(); Stack.Clear();
            Stack.Push(root);
            int budget = 60000;
            while (Stack.Count > 0 && budget-- > 0)
            {
                var e = Stack.Pop();
                if (wantIcons)
                {
                    var cv = e as Canvas;
                    if (cv != null && cv.Name == "UnitIcon") Icons.Add(cv);
                }
                if (wantTexts)
                {
                    var tb = e as TextBlock;
                    if (tb != null && WeaponStatusColors.Candidate(tb)) Texts.Add(tb);
                }
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
