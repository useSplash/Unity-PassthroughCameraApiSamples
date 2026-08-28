using System.Collections.Generic;
using UnityEngine;

namespace ConvaiRoom
{
    /// <summary>
    /// Builds the rounded-rectangle sprite the panel and its buttons are drawn with.
    ///
    /// Unity's UI has no corner radius: a rounded box is a sprite with 9-slice borders, and the
    /// options are to ship a PNG or to generate one. Generating wins here because the radius is
    /// then a number on <see cref="ScanPanelTheme"/> rather than a texture someone has to redraw
    /// -- and because the panel is already generated rather than authored, so there was no
    /// artwork to keep it company.
    ///
    /// The texture is only ever (2r+2) square. Everything between the corners is the 9-slice's
    /// stretched middle, so a 26-unit radius costs a 54x54 texture however big the panel it is
    /// painted across. Sprites are cached by radius and shared, which matters more than the
    /// pixels: a panel with three buttons, three plan buttons and two backgrounds asks for the
    /// same two radii sixteen times.
    /// </summary>
    public static class RoundedRectSprite
    {
        private static readonly Dictionary<int, Sprite> Cache = new Dictionary<int, Sprite>();

        /// <summary>
        /// A white rounded-rect sprite of the given corner radius, in pixels. Null at zero or
        /// less, which callers read as "leave it square" -- an Image with no sprite draws a
        /// plain rectangle, so that is the same code path with nothing special about it.
        ///
        /// The sprite is white so the Image's own colour tints it, exactly as the untextured
        /// rectangle did. Nothing here knows about the theme's colours.
        /// </summary>
        public static Sprite Get(float radius)
        {
            var pixels = Mathf.RoundToInt(radius);
            if (pixels <= 0) return null;

            // The == null is Unity's, not C#'s: a cached sprite whose texture was destroyed --
            // by a domain reload in the editor -- compares equal to null and has to be rebuilt.
            if (Cache.TryGetValue(pixels, out var cached) && cached != null) return cached;

            var sprite = Build(pixels);
            Cache[pixels] = sprite;
            return sprite;
        }

        private static Sprite Build(int radius)
        {
            // Two pixels of straight edge between the corners, which becomes the 9-slice's
            // stretched middle. One would do; two keeps the middle square and away from the
            // corners' antialiasing.
            var size = radius * 2 + 2;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"RoundedRect{radius}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,

                // Survives scene loads, and never lands in the project. These are generated on
                // demand and shared for the life of the app.
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = new Color32(255, 255, 255, Coverage(x, y, size, radius));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            // pixelsPerUnit 100 against the canvas's default referencePixelsPerUnit of 100 makes
            // one texture pixel one canvas unit, so the radius on the theme is in the same units
            // the layout is written in.
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                                       new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                                       new Vector4(radius, radius, radius, radius));

            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>
        /// How much of one pixel the rounded rectangle covers, 0 to 255.
        ///
        /// The shape is every point within <paramref name="radius"/> of the inner rectangle, so
        /// the distance to that rectangle -- found by clamping the pixel's centre into it -- is
        /// the only thing that has to be measured. Along a straight edge the clamp moves only
        /// one axis and the distance is a perpendicular one; in a corner it moves both and the
        /// distance becomes radial, which is what rounds it.
        ///
        /// The last half-pixel is a linear ramp rather than a hard cut. Without it the corners
        /// stair-step, and on a headset a jagged edge on a panel you are looking straight at is
        /// the first thing you notice.
        /// </summary>
        private static byte Coverage(int x, int y, int size, int radius)
        {
            var px = x + 0.5f;
            var py = y + 0.5f;

            var dx = px - Mathf.Clamp(px, radius, size - radius);
            var dy = py - Mathf.Clamp(py, radius, size - radius);

            var distance = Mathf.Sqrt(dx * dx + dy * dy);
            var alpha = Mathf.Clamp01(radius - distance + 0.5f);

            return (byte)Mathf.RoundToInt(alpha * 255f);
        }
    }
}
