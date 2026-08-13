using UnityEngine;

namespace RoomScan
{
    /// <summary>
    /// The floating caption over a scanned box.
    ///
    /// A TextMesh added via AddComponent has no font and no renderer material, so it
    /// draws nothing -- the font has to be pulled from the built-in resources and its
    /// material pushed onto the MeshRenderer by hand. Unity 2022 renamed that resource
    /// from Arial.ttf to LegacyRuntime.ttf, so both names are tried.
    /// </summary>
    public static class ScanLabel
    {
        private static Font _font;

        public static TextMesh Attach(Transform parent, float characterSize = 0.02f)
        {
            var go = new GameObject("label");
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<TextMesh>();
            text.characterSize = characterSize;
            text.fontSize = 96;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.color = Color.white;

            var font = BuiltinFont();
            if (font != null)
            {
                text.font = font;
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }

            go.AddComponent<FaceCamera>();
            return text;
        }

        private static Font BuiltinFont()
        {
            if (_font != null) return _font;

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            if (_font == null)
                Debug.LogWarning("[ScanLabel] No built-in font available; labels will be invisible.");

            return _font;
        }
    }
}
