using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Roulette.Client
{
    /// <summary>
    /// The wheel, the ball, and the spin.
    ///
    /// ## The bowl is a photograph, the pockets are not
    ///
    /// `wheel.png` supplies the wooden bowl, the brass studs, the worn metal and the
    /// centre turret. Its **pocket ring is covered over**, because it is not a real
    /// wheel: measured off the image, its frets sit 10.65 degrees apart, which is
    /// 33 or 34 pockets rather than 37. Landing the ball at the mathematically right
    /// angle on that ring would stop it on a number that did not win -- and that
    /// reads as a payout bug rather than an art one.
    ///
    /// So the coloured band from <see cref="ClothInner"/> to <see cref="ClothOuter"/>
    /// of the image radius -- measured by sampling the picture, not guessed -- is
    /// painted over with a ring generated from the server's own pocket list, with the
    /// numbers drawn on top. The pocket under the marker is then the pocket that won,
    /// by construction rather than by luck.
    ///
    /// ## Landing is computed, never hoped for
    ///
    /// The final angle is worked out first and the animation eases to it. Spinning at
    /// a decaying rate and stopping wherever it happens to stop lands a pocket out
    /// every so often, which is the same bug wearing a different hat.
    ///
    /// The trick that makes it exact is describing the ball's position **relative to
    /// the wheel**:
    ///
    ///     ballAngle = wheelAngle + pocketAngle(position) + drift
    ///
    /// where `drift` starts at several turns' worth and eases to exactly zero. Early
    /// on the drift dominates and the ball races round the rim against the wheel's
    /// direction; at the end it is zero, so the ball sits in its pocket and rides
    /// round with the wheel without any special case to put it there.
    /// </summary>
    internal static class WheelView
    {
        // ---- geometry measured off wheel.png, as fractions of its radius ----------
        //
        // Sampled rather than eyeballed: the coloured band runs from 0.40 to 0.71,
        // outside which is the wooden bowl and inside which is the hub and turret.
        // Covering exactly that band keeps everything with character in the picture.

        private const float ClothInner = 0.395f;
        private const float ClothOuter = 0.715f;

        /// <summary>Where the numbers sit, and where the ball rests once it settles.</summary>
        private const float PocketRadius = 0.545f;

        /// <summary>The rim the ball runs on before it drops in.</summary>
        private const float TrackRadius = 0.685f;

        /// <summary>
        /// Ball diameter as a fraction of the wheel's. A real ball is about 20mm on an
        /// 800mm wheel; a shade under a pocket's width so it sits in one rather than
        /// bridging two.
        /// </summary>
        private const float BallSize = 0.030f;

        private static readonly Color Felt = new Color(0.72f, 0.62f, 0.34f, 1f);

        private static RectTransform _wheel;
        private static RectTransform _ballPivot;
        private static RectTransform _ball;
        private static TMP_FontAsset _font;

        private static float _diameter;
        private static int _pocketCount = 37;

        /// <summary>Where the wheel is now, so a new spin starts from where the last one stopped.</summary>
        private static float _wheelAngle;

        private static Coroutine _spinning;

        internal static bool IsSpinning => _spinning != null;

        private static string ArtPath(string file) => Path.Combine(
            Path.GetDirectoryName(RouletteClientPlugin.Instance?.Info?.Location ?? ".") ?? ".", file);

        /// <summary>
        /// Builds the wheel from the pockets the server sent.
        ///
        /// Rebuilt whenever the pocket list changes rather than cached: a European and
        /// an American wheel are different objects, and the list is the only thing
        /// that says which is on the table.
        /// </summary>
        internal static GameObject Build(Transform parent, IReadOnlyList<PocketInfo> pockets, float diameter, TMP_FontAsset font)
        {
            _font = font;
            _diameter = diameter;
            _pocketCount = Math.Max(1, pockets.Count);

            var root = NewImage("Wheel", parent, Color.white);
            root.sizeDelta = new Vector2(diameter, diameter);

            // The bowl. It does not rotate with the pockets in real life either -- the
            // bowl is furniture and the head spins inside it -- but here the whole
            // picture turns, because the photograph's bowl and its head are one image.
            var bowl = root.GetComponent<Image>();
            var art = Textures.FromFile(ArtPath("wheel.png"));

            if (art != null)
            {
                bowl.sprite = art;
                bowl.preserveAspect = true;
            }
            else
            {
                bowl.color = new Color(0.16f, 0.11f, 0.07f, 1f);
                RouletteClientPlugin.Log.LogWarning(
                    "[Roulette] no wheel.png beside the plugin; drawing the wheel without its bowl.");
            }

            // Everything that has to line up with a pocket lives under here, so one
            // rotation moves the colours, the numbers and the resting ball together.
            _wheel = NewImage("Head", root, Color.white);
            _wheel.anchorMin = _wheel.anchorMax = new Vector2(0.5f, 0.5f);
            _wheel.pivot = new Vector2(0.5f, 0.5f);
            _wheel.sizeDelta = new Vector2(diameter, diameter);
            _wheel.GetComponent<Image>().sprite = PocketRing(pockets);

            BuildNumbers(pockets, diameter);
            BuildMarker(root, diameter);
            BuildBall(root, diameter);

            _wheelAngle = 0f;
            Apply(_wheelAngle, _wheelAngle, TrackRadius);

            return root.gameObject;
        }

        /// <summary>
        /// Spins to a pocket and lands on it.
        ///
        /// <paramref name="position"/> is the winning pocket's place on the wheel,
        /// clockwise from the single zero -- the server sends it precisely so this does
        /// not have to know the wheel order.
        /// </summary>
        internal static void Spin(int position, Action onFinished)
        {
            var host = RouletteClientPlugin.Instance;

            if (host == null || _wheel == null)
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
            const float duration = 6.5f;

            // The head slows over the whole spin; the ball is still running when the
            // head has nearly stopped, which is what a real one looks like.
            var wheelFrom = _wheelAngle;
            var wheelTo = wheelFrom + (360f * 3.25f);

            // Whole turns of relative motion, so the ball laps the wheel several times
            // before it drops. Negative: the ball runs against the head.
            var drift = -360f * 11f;

            var landAt = PocketAngle(position);
            var elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                var t = Mathf.Clamp01(elapsed / duration);

                var wheel = Mathf.Lerp(wheelFrom, wheelTo, EaseOut(t, 2.2f));

                // Drift eases to exactly zero, which is what puts the ball in its
                // pocket without a special case at the end.
                var remaining = drift * (1f - EaseOut(t, 3.4f));

                var ball = wheel + landAt + remaining + Skip(t);

                Apply(wheel, ball, BallRadius(t));

                yield return null;
            }

            _wheelAngle = wheelTo;
            Apply(wheelTo, wheelTo + landAt, PocketRadius);

            _spinning = null;
            onFinished?.Invoke();
        }

        /// <summary>
        /// The bouncing, which is decoration with one hard rule: it must be exactly
        /// zero at the end.
        ///
        /// A damped oscillation whose envelope is driven to nothing by the same `t`
        /// that ends the spin, so however lively it looks on the way down it cannot
        /// move where the ball finishes. Anything that added a random nudge at the end
        /// would be a ball that lands a pocket out at random -- which is precisely the
        /// failure all this arithmetic exists to avoid.
        /// </summary>
        private static float Skip(float t)
        {
            // Nothing until the ball leaves the track: it is smooth up there.
            if (t < 0.55f)
            {
                return 0f;
            }

            var u = (t - 0.55f) / 0.45f;
            var envelope = (1f - u) * (1f - u) * 26f;

            // Two frequencies: the long one is the ball crossing the frets, the short
            // one is it rattling in a pocket before it settles.
            return envelope * (Mathf.Sin(u * 17f) * 0.75f + Mathf.Sin(u * 41f) * 0.25f);
        }

        /// <summary>
        /// The drop. The ball runs the outer track, then falls inward across the frets
        /// with a couple of bounces before settling into the pocket ring.
        /// </summary>
        private static float BallRadius(float t)
        {
            if (t < 0.55f)
            {
                return TrackRadius;
            }

            var u = (t - 0.55f) / 0.45f;
            var fall = Mathf.SmoothStep(TrackRadius, PocketRadius, u);

            // Two decaying bounces back up the slope, because a ball that slides
            // straight down reads as a bead on a wire.
            var bounce = Mathf.Abs(Mathf.Sin(u * 6.5f)) * (1f - u) * (1f - u) * 0.055f;

            return fall + bounce;
        }

        private static void Apply(float wheelAngle, float ballAngle, float ballRadius)
        {
            if (_wheel == null)
            {
                return;
            }

            // Negated because Unity turns counter-clockwise and a wheel's numbers run
            // clockwise from the zero.
            _wheel.localRotation = Quaternion.Euler(0f, 0f, -wheelAngle);

            if (_ballPivot == null || _ball == null)
            {
                return;
            }

            _ballPivot.localRotation = Quaternion.Euler(0f, 0f, -ballAngle);
            _ball.anchoredPosition = new Vector2(0f, ballRadius * _diameter * 0.5f);
        }

        private static float PocketAngle(int position) => position * (360f / _pocketCount);

        /// <summary>Fast at first, crawling at the end, which is how a wheel stops.</summary>
        private static float EaseOut(float t, float power) => 1f - Mathf.Pow(1f - t, power);

        // ---------------------------------------------------------------- building

        /// <summary>
        /// Paints the pocket ring from the server's list.
        ///
        /// Generated rather than shipped, so the colours and their order cannot
        /// disagree with the wheel the server is actually settling against. The
        /// texture is drawn once per table.
        /// </summary>
        private static Sprite PocketRing(IReadOnlyList<PocketInfo> pockets)
        {
            const int size = 1024;
            var half = size / 2f;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[size * size];
            var clear = new Color32(0, 0, 0, 0);

            var red = new Color32(122, 24, 22, 255);
            var black = new Color32(24, 23, 22, 255);
            var green = new Color32(20, 78, 44, 255);
            var fret = new Color32(150, 126, 74, 255);

            var inner = ClothInner * size;
            var outer = ClothOuter * size;
            var step = 360f / pockets.Count;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - half;
                    var dy = y - half;
                    var r = Mathf.Sqrt((dx * dx) + (dy * dy));

                    if (r < inner || r > outer)
                    {
                        pixels[(y * size) + x] = clear;
                        continue;
                    }

                    // Clockwise from straight up, matching the wheel order and the
                    // photograph, whose green zero sits at the top.
                    var angle = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                    if (angle < 0f)
                    {
                        angle += 360f;
                    }

                    var index = Mathf.Clamp((int)(angle / step), 0, pockets.Count - 1);
                    var within = (angle / step) - index;

                    // A fret between every pocket, and a lip top and bottom, so the
                    // ring reads as metal-divided rather than as a pie chart.
                    var edge = within < 0.045f || within > 0.955f
                               || r < inner + 6f || r > outer - 6f;

                    pixels[(y * size) + x] = edge
                        ? fret
                        : pockets[index].Colour switch
                        {
                            "Red" => red,
                            "Green" => green,
                            _ => black,
                        };
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>
        /// The numbers, as labels parented to the head so they turn with it.
        ///
        /// Drawn as text rather than baked into the ring texture because text baked at
        /// this size goes to mush, and because a label can be rotated to sit radially
        /// the way the numbers on a real wheel do.
        /// </summary>
        private static void BuildNumbers(IReadOnlyList<PocketInfo> pockets, float diameter)
        {
            var step = 360f / pockets.Count;

            for (var i = 0; i < pockets.Count; i++)
            {
                var label = new GameObject("N" + pockets[i].Label, typeof(RectTransform));
                label.transform.SetParent(_wheel, false);

                var text = label.AddComponent<TextMeshProUGUI>();
                text.text = pockets[i].Label;
                text.fontSize = diameter * 0.032f;
                text.alignment = TextAlignmentOptions.Center;
                text.color = new Color(0.94f, 0.92f, 0.86f, 1f);
                text.raycastTarget = false;

                if (_font != null)
                {
                    text.font = _font;
                }

                var rect = (RectTransform)label.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(diameter * 0.09f, diameter * 0.05f);

                var angle = i * step;
                var radians = (90f - angle) * Mathf.Deg2Rad;
                var radius = PocketRadius * diameter * 0.5f;

                rect.anchoredPosition = new Vector2(
                    Mathf.Cos(radians) * radius,
                    Mathf.Sin(radians) * radius);

                // Turned so each number faces out of the wheel, feet towards the hub.
                rect.localRotation = Quaternion.Euler(0f, 0f, -angle);
            }
        }

        /// <summary>
        /// The marker at the top, which is where the winning pocket comes to rest.
        ///
        /// Outside the head so it does not turn: it is the fixed point the whole
        /// animation is aimed at, and a spinning marker would mean nothing.
        /// </summary>
        private static void BuildMarker(RectTransform root, float diameter)
        {
            var marker = NewImage("Marker", root, Felt);
            marker.anchorMin = marker.anchorMax = new Vector2(0.5f, 0.5f);
            marker.pivot = new Vector2(0.5f, 0.5f);
            marker.sizeDelta = new Vector2(diameter * 0.022f, diameter * 0.075f);
            marker.anchoredPosition = new Vector2(0f, diameter * 0.5f * 0.755f);
            marker.GetComponent<Image>().sprite = Textures.RoundedBox(4, Felt, new Color(0.2f, 0.16f, 0.08f, 1f), 2);
            marker.GetComponent<Image>().type = Image.Type.Sliced;
        }

        private static void BuildBall(RectTransform root, float diameter)
        {
            // A pivot at the centre that carries the ball out at a radius: rotating the
            // pivot walks the ball round the rim, which is one transform instead of the
            // trigonometry every frame.
            _ballPivot = NewImage("BallPivot", root, Color.clear);
            _ballPivot.anchorMin = _ballPivot.anchorMax = new Vector2(0.5f, 0.5f);
            _ballPivot.pivot = new Vector2(0.5f, 0.5f);
            _ballPivot.sizeDelta = new Vector2(diameter, diameter);

            _ball = NewImage("Ball", _ballPivot, Color.white);
            _ball.anchorMin = _ball.anchorMax = new Vector2(0.5f, 0.5f);
            _ball.pivot = new Vector2(0.5f, 0.5f);
            _ball.sizeDelta = new Vector2(diameter * BallSize, diameter * BallSize);

            var image = _ball.GetComponent<Image>();
            var art = Textures.FromFile(ArtPath("ball.png"));

            if (art != null)
            {
                image.sprite = art;
                image.preserveAspect = true;
            }
            else
            {
                // Drawn rather than missing: a spin with no ball in it is unreadable.
                image.sprite = Textures.RoundedBox(
                    64, new Color(0.94f, 0.92f, 0.78f, 1f), new Color(0.6f, 0.58f, 0.48f, 1f), 2);
                image.type = Image.Type.Sliced;
            }

            // Above the ring and the numbers whatever order they were built in.
            _ballPivot.SetAsLastSibling();

            var shadow = _ball.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.55f);
            shadow.effectDistance = new Vector2(2f, -3f);
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
