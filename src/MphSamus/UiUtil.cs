// UiUtil - small UI helpers: Unity's built-in font, object paths and hierarchy dumps for the log.
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MphSamus
{
    public static class UiUtil
    {
        private static string LabelOf(GameObject go)
        {
            TMP_Text t = go.GetComponentInChildren<TMP_Text>(true);
            string raw = t != null ? t.text : go.GetComponentInChildren<Text>(true)?.text ?? "";
            // drop rich-text tags like <mark=#000000>
            return System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]*>", "").Trim().ToUpperInvariant();
        }

        public static Font BuiltinFont()
        {
            // Unity 2022.2+ calls the built-in font LegacyRuntime.ttf (Arial.ttf throws)
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { }
            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
            catch { return null; }
        }

        public static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent) { sb.Insert(0, p.name + "/"); }
            return sb.ToString();
        }

        public static string Dump(Transform t, int depth, int maxDepth)
        {
            if (t == null) { return ""; }
            var sb = new StringBuilder();
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (inactive)");
            var comps = new List<string>();
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c != null && !(c is Transform)) { comps.Add(c.GetType().Name); }
            }
            if (comps.Count > 0) { sb.Append(" [").Append(string.Join(", ", comps)).Append(']'); }
            string label = LabelOf(t.gameObject);
            if (t.GetComponent<TMP_Text>() != null || t.GetComponent<Text>() != null) { sb.Append(" \"").Append(label).Append('"'); }
            sb.Append(" layer ").Append(t.gameObject.layer).Append('\n');
            if (depth < maxDepth)
            {
                foreach (Transform c in t) { sb.Append(Dump(c, depth + 1, maxDepth)); }
            }
            return sb.ToString();
        }
    }
}
