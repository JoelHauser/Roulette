using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Roulette.Client
{
    /// <summary>
    /// The wheel, the ball, and the spin.
    ///
    /// ## Drawn, not photographed
    ///
    /// This was a photograph with a generated pocket ring painted over it, and it was
    /// wrong twice. The photograph had 33 or 34 pockets rather than 37, so the ball
    /// could never have landed on the number that won; and the overlay was drawn with
    /// its radii measured against the image's **diameter** where they meant its
    /// radius, so the ring came out at twice the size and burst out of the bowl as a
    /// starburst.
    ///
    /// Everything is drawn now. That fixes both at once -- 37 pockets because the
    /// server sent 37, at the radius asked for -- and it costs nothing in looks: a
    /// wheel is concentric rings, which is exactly what a texture generator is good at.
    ///
    /// ## The bowl is still, the head turns
    ///
    /// A real wheel is two pieces. The **bowl** -- the outer rim and the track the ball
    /// runs on -- is furniture and never moves. The **head** -- the pockets, the
    /// numbers, the cone -- spins inside it. Drawing them as one picture, as this did,
    /// meant the ball's track rotated under the ball, which is why the counter-rotation
    /// never read.
    ///
    /// So they are two images now. The ball orbits the still bowl one way while the
    /// head turns the other beneath it, then drops in.
    ///
    /// ## Landing is computed, never hoped for
    ///
    /// The ball's position is described **relative to the head**:
    ///
    ///     ballAngle = headAngle + pocketAngle(position) + relative
    ///
    /// where `relative` starts at a dozen turns and decays to exactly zero. Early on
    /// its decay outruns the head, so the ball visibly runs the other way; at the end
    /// it is zero, so the ball sits in its pocket and rides round -- with no special
    /// case to put it there and nothing to round off.
    ///
    /// The decay is the physics. Angular velocity under friction falls off
    /// exponentially, so the relative angle is an exponential settling on its target,
    /// and the bounces are a damped oscillation on top. Both reach zero at the same
    /// instant the spin ends, so however lively it looks it cannot change where the
    /// ball finishes.
    /// </summary>
    internal static class WheelView
    {
        // ---- geometry, all as fractions of the wheel's RADIUS ---------------------
        //
        // Read down from the outside. The bowl owns everything above ApronInner, the
        // head everything below it.

        private const float RimInner = 0.90f;
        private const float TrackInner = 0.795f;
        private const float ApronInner = 0.735f;

        private const float PocketInner = 0.50f;
        private const float NumberRadius = 0.605f;
        private const float HubRing = 0.46f;

        /// <summary>Where the ball runs before it drops: the middle of the track.</summary>
        private const float TrackRadius = 0.845f;

        /// <summary>Where it comes to rest: the outer part of a pocket.</summary>
        private const float RestRadius = 0.645f;

        /// <summary>
        /// Ball diameter over wheel diameter. A pocket is about 0.055 of the diameter
        /// wide at the resting radius, and a ball that fills three quarters of one sits
        /// in a pocket rather than bridging two.
        /// </summary>
        private const float BallSize = 0.040f;

        private const int Texture = 1024;

        // ---- palette ---------------------------------------------------------------

        private static readonly Color32 RimDark = new Color32(28, 22, 17, 255);
        private static readonly Color32 RimLight = new Color32(63, 51, 38, 255);
        private static readonly Color32 TrackDark = new Color32(38, 31, 24, 255);
        private static readonly Color32 TrackLight = new Color32(74, 61, 45, 255);
        private static readonly Color32 Apron = new Color32(46, 38, 29, 255);
        private static readonly Color32 Gold = new Color32(184, 154, 92, 255);
        private static readonly Color32 GoldDim = new Color32(120, 99, 58, 255);
        private static readonly Color32 Red = new Color32(140, 27, 27, 255);
        private static readonly Color32 Black = new Color32(26, 25, 27, 255);
        private static readonly Color32 Green = new Color32(20, 96, 58, 255);
        private static readonly Color32 ConeLight = new Color32(112, 93, 62, 255);
        private static readonly Color32 ConeDark = new Color32(40, 33, 23, 255);
        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        private static readonly Color Ink = new Color(0.95f, 0.93f, 0.87f, 1f);

        private static RectTransform _head;
        private static RectTransform _ballPivot;
        private static RectTransform _ball;
        private static TMP_FontAsset _font;

        private static float _diameter;
        private static int _pocketCount = 37;
        private static float _headAngle;

        private static Coroutine _spinning;

        internal static bool IsSpinning => _spinning != null;

        internal static GameObject Build(
            Transform parent, IReadOnlyList<PocketInfo> pockets, float diameter, TMP_FontAsset font)
        {
            _font = font;
            _diameter = diameter;
            _pocketCount = Mathf.Max(1, pockets.Count);
            _headAngle = 0f;

            var root = NewImage("Wheel", parent, Clear);
            root.sizeDelta = new Vector2(diameter, diameter);

            // The head goes down first so the bowl's inner lip draws over its edge,
            // which is what makes the pockets look sunk into the bowl rather than
            // pasted on top of it.
            _head = NewImage("Head", root, Color.white);
            _head.anchorMin = _head.anchorMax = new Vector2(0.5f, 0.5f);
            _head.pivot = new Vector2(0.5f, 0.5f);
            _head.sizeDelta = new Vector2(diameter, diameter);
            _head.GetComponent<Image>().sprite = HeadSprite(pockets);

            BuildNumbers(pockets, diameter);

            var bowl = NewImage("Bowl", root, Color.white);
            bowl.anchorMin = bowl.anchorMax = new Vector2(0.5f, 0.5f);
            bowl.pivot = new Vector2(0.5f, 0.5f);
            bowl.sizeDelta = new Vector2(diameter, diameter);
            bowl.GetComponent<Image>().sprite = BowlSprite();

            BuildBall(root, diameter);
            BuildMarker(root, diameter);

            Apply(_headAngle, _headAngle, TrackRadius);

            return root.gameObject;
        }

        /// <summary>
        /// Spins to a pocket and lands on it. <paramref name="position"/> is the
        /// winning pocket's place on the wheel, clockwise from the single zero.
        /// </summary>
        internal static void Spin(int position, Action onFinished)
        {
            var host = RouletteClientPlugin.Instance;

            if (host == null || _head == null)
            {
                onFinished?.Invoke();
                return;
            }

            if (_spinning != null)
            {
                host.StopCoroutine(_spinning);
            }

            _spinning = host.StartCoroutine(Run(position, onFinished));
        }

        private static IEnumerator Run(int position, Action onFinished)
        {
            const float duration = 7.5f;

            // Where the ball leaves the track and starts falling in.
            const float drop = 0.62f;

            var headFrom = _headAngle;
            var headTo = headFrom + (360f * 4.25f);

            // How far the ball travels relative to the head. Positive, which is what
            // makes it run against the head's direction: the head turns one way at a
            // steady ease while this unwinds the other way much faster.
            const float relative = 360f * 13f;

            var landAt = PocketAngle(position);
            var elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                var t = Mathf.Clamp01(elapsed / duration);

                var head = Mathf.Lerp(headFrom, headTo, EaseOut(t, 2.4f));
                var ball = head + landAt + (relative * Friction(t)) + Rattle(t, drop);

                Apply(head, ball, Radius(t, drop));

                yield return null;
            }

            _headAngle = headTo;
            Apply(headTo, headTo + landAt, RestRadius);

            _spinning = null;
            onFinished?.Invoke();
        }

        /// <summary>
        /// What is left of the ball's journey, from 1 down to exactly 0.
        ///
        /// A ball on a track loses speed to friction, so its angular velocity decays
        /// exponentially and the distance still to travel decays with it. Shifted and
        /// scaled so it is precisely 0 at the end -- an exponential never actually
        /// arrives, and "nearly there" is a ball resting a fraction off its pocket.
        /// </summary>
        private static float Friction(float t)
        {
            const float k = 4.2f;

            var e = Mathf.Exp(-k * t);
            var end = Mathf.Exp(-k);

            return (e - end) / (1f - end);
        }

        /// <summary>
        /// The bouncing, which is decoration with one hard rule: exactly zero at the
        /// end.
        ///
        /// Nothing while the ball is still on the track -- it is smooth up there. Once
        /// it drops it crosses the frets, and two frequencies stand in for that: a slow
        /// one for skipping across pockets and a fast one for rattling inside the one
        /// it settles in. The envelope is driven to nothing by the same clock that ends
        /// the spin, so a lively bounce cannot move where the ball finishes.
        /// </summary>
        private static float Rattle(float t, float drop)
        {
            if (t < drop)
            {
                return 0f;
            }

            var u = (t - drop) / (1f - drop);
            var envelope = (1f - u) * (1f - u) * 22f;

            return envelope * ((Mathf.Sin(u * 15f) * 0.72f) + (Mathf.Sin(u * 37f) * 0.28f));
        }

        /// <summary>
        /// How far out the ball is. It holds the track while it is fast, then falls
        /// inward, bouncing back up the slope a couple of times on the way -- a ball
        /// that slides straight down reads as a bead on a wire.
        /// </summary>
        private static float Radius(float t, float drop)
        {
            if (t < drop)
            {
                return TrackRadius;
            }

            var u = (t - drop) / (1f - drop);
            var fall = Mathf.SmoothStep(TrackRadius, RestRadius, u);
            var bounce = Mathf.Abs(Mathf.Sin(u * 7f)) * (1f - u) * (1f - u) * 0.075f;

            return fall + bounce;
        }

        private static void Apply(float headAngle, float ballAngle, float ballRadius)
        {
            if (_head == null)
            {
                return;
            }

            // Negated because Unity turns counter-clockwise and a wheel's numbers run
            // clockwise from the zero.
            _head.localRotation = Quaternion.Euler(0f, 0f, -headAngle);

            if (_ballPivot == null || _ball == null)
            {
                return;
            }

            _ballPivot.localRotation = Quaternion.Euler(0f, 0f, -ballAngle);
            _ball.anchoredPosition = new Vector2(0f, ballRadius * _diameter * 0.5f);
        }

        private static float PocketAngle(int position) => position * (360f / _pocketCount);

        private static float EaseOut(float t, float power) => 1f - Mathf.Pow(1f - t, power);

        // ---------------------------------------------------------------- the drawing

        /// <summary>
        /// The bowl: the outer rim and the track the ball runs on. Transparent inside,
        /// where the head shows through.
        /// </summary>
        private static Sprite BowlSprite()
        {
            return Paint((f, angle, light) =>
            {
                if (f > 1f)
                {
                    return Clear;
                }

                if (f > RimInner)
                {
                    // The rim, lit from the top left and darkening to its outer edge.
                    var g = Mathf.InverseLerp(1f, RimInner, f);
                    return Shade(Lerp(RimDark, RimLight, g * 0.75f), light);
                }

                if (f > TrackInner)
                {
                    // The track. Darker where it meets the rim and brighter towards the
                    // inside, so it reads as a channel the ball sits in rather than a
                    // flat band.
                    var g = Mathf.InverseLerp(RimInner, TrackInner, f);
                    var c = Lerp(TrackDark, TrackLight, Mathf.Sin(g * Mathf.PI) * 0.9f);
                    return Shade(c, light);
                }

                if (f > ApronInner)
                {
                    // The apron sloping down to the pockets, with a gold lip at the
                    // bottom of it.
                    var g = Mathf.InverseLerp(TrackInner, ApronInner, f);
                    var c = Lerp(Apron, GoldDim, Mathf.SmoothStep(0f, 1f, g) * 0.55f);
                    return Shade(c, light);
                }

                return Clear;
            });
        }

        /// <summary>
        /// The head: the pockets, their frets, and the cone. Drawn from the pocket list
        /// the server sent, so the colours and their order cannot disagree with the
        /// wheel it is settling against.
        /// </summary>
        private static Sprite HeadSprite(IReadOnlyList<PocketInfo> pockets)
        {
            var step = 360f / pockets.Count;
            var half = Texture / 2f;

            return Paint((f, angle, light) =>
            {
                if (f > ApronInner)
                {
                    return Clear;
                }

                if (f > PocketInner)
                {
                    var index = Mathf.Clamp((int)(angle / step), 0, pockets.Count - 1);

                    // Frets are a constant thickness in pixels, so their angular width
                    // has to grow as the radius shrinks or they would taper to nothing
                    // at the inside of the ring.
                    var pixels = Mathf.Max(f * half, 1f);
                    var fret = 2.6f / pixels * Mathf.Rad2Deg;

                    var offset = angle - (index * step);
                    var edge = Mathf.Min(offset, step - offset);

                    var pocket = pockets[index].Colour switch
                    {
                        "Red" => Red,
                        "Green" => Green,
                        _ => Black,
                    };

                    // A little darker towards the hub, so the pockets have depth.
                    var depth = Mathf.InverseLerp(PocketInner, ApronInner, f);
                    pocket = Lerp(Scale(pocket, 0.55f), pocket, depth);

                    var onFret = Smooth(edge, fret, fret + (0.9f / pixels * Mathf.Rad2Deg));
                    var lip = Smooth(f, ApronInner - 0.012f, ApronInner - 0.004f);

                    var c = Lerp(Gold, pocket, onFret);
                    c = Lerp(c, GoldDim, lip);

                    return Shade(c, light);
                }

                if (f > HubRing)
                {
                    return Shade(Gold, light);
                }

                // The cone, and the turret sitting on it.
                var cone = Lerp(ConeLight, ConeDark, Mathf.InverseLerp(0f, HubRing, f));

                if (f < 0.055f)
                {
                    cone = Lerp(Gold, GoldDim, f / 0.055f);
                }
                else if (f < 0.075f)
                {
                    cone = GoldDim;
                }
                else if (f < 0.30f)
                {
                    // Four spokes out of the turret, which is what stops the middle of
                    // the wheel looking like a plain disc while it turns.
                    var spoke = Mathf.Abs(Mathf.Sin(angle * Mathf.Deg2Rad * 2f));
                    cone = Lerp(Lerp(cone, GoldDim, 0.75f), cone, Smooth(spoke, 0.05f, 0.22f));
                }

                return Shade(cone, light);
            });
        }

        /// <summary>
        /// Runs a function over every pixel of a square texture, handing it the radius
        /// as a fraction, the angle clockwise from the top, and a lighting factor.
        ///
        /// The fraction is of the **radius**, not the diameter. Getting that wrong is
        /// what drew the last pocket ring at twice its size.
        /// </summary>
        private static Sprite Paint(Func<float, float, float, Color32> shade)
        {
            var texture = new Texture2D(Texture, Texture, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[Texture * Texture];
            var half = Texture / 2f;

            for (var y = 0; y < Texture; y++)
            {
                var dy = y - half;

                for (var x = 0; x < Texture; x++)
                {
                    var dx = x - half;
                    var f = Mathf.Sqrt((dx * dx) + (dy * dy)) / half;

                    if (f > 1.002f)
                    {
                        pixels[(y * Texture) + x] = Clear;
                        continue;
                    }

                    var angle = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                    if (angle < 0f)
                    {
                        angle += 360f;
                    }

                    // A soft key light from the upper left, and a gentle darkening
                    // towards the edge. Together they stop the rings reading flat.
                    var nx = dx / half;
                    var ny = dy / half;
                    var light = 1f + (0.20f * ((-nx * 0.65f) + (ny * 0.75f))) - (0.14f * f * f);

                    var c = shade(f, angle, light);

                    // Feather the very edge of the disc so it is not a jagged circle.
                    if (c.a > 0 && f > 0.99f)
                    {
                        c.a = (byte)(c.a * Mathf.Clamp01((1.002f - f) / 0.012f));
                    }

                    pixels[(y * Texture) + x] = c;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return Sprite.Create(
                texture, new Rect(0f, 0f, Texture, Texture), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Color32 Lerp(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);

            return new Color32(
                (byte)(a.r + ((b.r - a.r) * t)),
                (byte)(a.g + ((b.g - a.g) * t)),
                (byte)(a.b + ((b.b - a.b) * t)),
                (byte)(a.a + ((b.a - a.a) * t)));
        }

        private static Color32 Scale(Color32 c, float by) =>
            new Color32((byte)(c.r * by), (byte)(c.g * by), (byte)(c.b * by), c.a);

        private static Color32 Shade(Color32 c, float light)
        {
            light = Mathf.Clamp(light, 0.55f, 1.45f);

            return new Color32(
                (byte)Mathf.Clamp(c.r * light, 0f, 255f),
                (byte)Mathf.Clamp(c.g * light, 0f, 255f),
                (byte)Mathf.Clamp(c.b * light, 0f, 255f),
                c.a);
        }

        private static float Smooth(float v, float a, float b) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, v));

        // ---------------------------------------------------------------- the pieces

        /// <summary>
        /// The numbers, as labels parented to the head so they turn with it. Text
        /// rather than baked into the ring, because text baked at this size goes to
        /// mush and a label can be turned to sit radially the way a real wheel's does.
        /// </summary>
        private static void BuildNumbers(IReadOnlyList<PocketInfo> pockets, float diameter)
        {
            var step = 360f / pockets.Count;

            for (var i = 0; i < pockets.Count; i++)
            {
                var go = new GameObject("N" + pockets[i].Label, typeof(RectTransform));
                go.transform.SetParent(_head, false);

                var text = go.AddComponent<TextMeshProUGUI>();
                text.text = pockets[i].Label;
                text.fontSize = diameter * 0.031f;
                text.alignment = TextAlignmentOptions.Center;
                text.color = Ink;
                text.raycastTarget = false;
                text.enableWordWrapping = false;

                if (_font != null)
                {
                    text.font = _font;
                }

                var rect = (RectTransform)go.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(diameter * 0.085f, diameter * 0.05f);

                var angle = i * step;
                var radians = (90f - angle) * Mathf.Deg2Rad;
                var radius = NumberRadius * diameter * 0.5f;

                rect.anchoredPosition = new Vector2(
                    Mathf.Cos(radians) * radius, Mathf.Sin(radians) * radius);

                // Feet towards the hub, so every number reads from the outside in.
                rect.localRotation = Quaternion.Euler(0f, 0f, -angle);
            }
        }

        /// <summary>
        /// The marker the winning pocket comes to rest under. Outside the head so it
        /// does not turn: it is the fixed point the whole animation is aimed at, and a
        /// spinning marker would mean nothing.
        /// </summary>
        private static void BuildMarker(RectTransform root, float diameter)
        {
            var marker = NewImage("Marker", root, Color.white);
            marker.anchorMin = marker.anchorMax = new Vector2(0.5f, 0.5f);
            marker.pivot = new Vector2(0.5f, 1f);
            marker.sizeDelta = new Vector2(diameter * 0.024f, diameter * 0.062f);
            marker.anchoredPosition = new Vector2(0f, diameter * 0.5f * 1.01f);

            var image = marker.GetComponent<Image>();
            image.sprite = Textures.RoundedBox(
                5, new Color(0.80f, 0.69f, 0.38f, 1f), new Color(0.16f, 0.13f, 0.08f, 1f), 2);
            image.type = Image.Type.Sliced;
        }

        private static void BuildBall(RectTransform root, float diameter)
        {
            // A pivot at the centre carrying the ball out at a radius: rotating the
            // pivot walks the ball round the track, which is one transform instead of
            // trigonometry every frame.
            _ballPivot = NewImage("BallPivot", root, Clear);
            _ballPivot.anchorMin = _ballPivot.anchorMax = new Vector2(0.5f, 0.5f);
            _ballPivot.pivot = new Vector2(0.5f, 0.5f);
            _ballPivot.sizeDelta = new Vector2(diameter, diameter);

            _ball = NewImage("Ball", _ballPivot, Color.white);
            _ball.anchorMin = _ball.anchorMax = new Vector2(0.5f, 0.5f);
            _ball.pivot = new Vector2(0.5f, 0.5f);
            _ball.sizeDelta = new Vector2(diameter * BallSize, diameter * BallSize);

            var image = _ball.GetComponent<Image>();
            var art = Textures.FromFile(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(RouletteClientPlugin.Instance?.Info?.Location ?? ".") ?? ".",
                "ball.png"));

            if (art != null)
            {
                image.sprite = art;
                image.preserveAspect = true;
            }
            else
            {
                // Drawn rather than missing: a spin with no ball in it is unreadable.
                image.sprite = Textures.RoundedBox(
                    64, new Color(0.95f, 0.93f, 0.83f, 1f), new Color(0.55f, 0.52f, 0.44f, 1f), 2);
                image.type = Image.Type.Sliced;
            }

            var shadow = _ball.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            shadow.effectDistance = new Vector2(2f, -3f);

            _ballPivot.SetAsLastSibling();
        }

        private static RectTransform NewImage(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;

            return (RectTransform)go.transform;
        }
    }

    /// <summary>One pocket as the server described it. See TableView.PocketView.</summary>
    internal sealed class PocketInfo
    {
        internal PocketInfo(int number, string label, string colour)
        {
            Number = number;
            Label = label;
            Colour = colour;
        }

        internal int Number { get; }

        internal string Label { get; }

        internal string Colour { get; }
    }
}
