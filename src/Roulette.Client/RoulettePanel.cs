using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Roulette.Client
{
    /// <summary>
    /// The table window.
    ///
    /// A first pass built around the wheel: the wheel spins, the ball lands, and there
    /// is enough of a control strip to put chips on a few spots and turn it. The full
    /// betting cloth comes next -- proving the spin lands where the server said is
    /// worth doing before a hundred betting spots are drawn on top of it.
    ///
    /// The server decides everything. The panel renders the view it is handed and
    /// posts what the player pressed.
    /// </summary>
    internal static class RoulettePanel
    {
        private const string RootName = "RouletteTableCanvas";

        private const float WheelSize = 780f;

        private static readonly Color Gold = new Color(0.72f, 0.62f, 0.34f, 1f);
        private static readonly Color Ink = new Color(0.88f, 0.86f, 0.80f, 1f);

        private static GameObject _root;
        private static TMP_FontAsset _font;

        private static RectTransform _wheelHolder;
        private static RectTransform _actionRow;
        private static TextMeshProUGUI _status;
        private static TextMeshProUGUI _result;

        private static JObject _lastReply;
        private static string _pocketSignature;

        /// <summary>What one chip is worth. The table minimum, until there is a chip tray.</summary>
        private static int _chip = 10_000;

        internal static bool IsOpen => _root != null && _root.activeSelf;

        internal static void Toggle()
        {
            if (IsOpen)
            {
                Close();
                return;
            }

            Open();
        }

        internal static void Open()
        {
            try
            {
                if (_root == null)
                {
                    Build();
                }

                if (_root == null)
                {
                    return;
                }

                _root.SetActive(true);

                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_root.transform);

                Render(RouletteApi.State());
            }
            catch (Exception ex)
            {
                RouletteClientPlugin.Log.LogError("[Roulette] could not open the table: " + ex);
            }
        }

        internal static void Close()
        {
            if (_root != null)
            {
                _root.SetActive(false);
            }
        }

        internal static void OnEscape()
        {
            if (_root == null || !_root.activeSelf)
            {
                return;
            }

            // Not while the wheel is turning. The result is already settled on the
            // server, so nothing is lost by closing -- but a table that vanishes
            // mid-spin looks like a crash.
            if (WheelView.IsSpinning)
            {
                return;
            }

            Close();
        }

        // ---------------------------------------------------------------- actions

        private static void Place(string kind, int selection)
        {
            var reply = RouletteApi.Place(kind, selection, _chip);

            if (reply == null)
            {
                SetStatus("No answer from the server.");
                return;
            }

            Render(reply);

            var error = (string)reply["Error"];
            if (!string.IsNullOrEmpty(error))
            {
                SetStatus(error);
            }
        }

        private static void Clear() => Render(RouletteApi.Clear());

        /// <summary>
        /// Turns the wheel.
        ///
        /// The server settles before this returns, so the animation is played over a
        /// result that already exists. The buttons go away while it runs -- not to
        /// protect the money, which is already safe, but because a table that accepts
        /// bets during a spin is lying about what it is doing.
        /// </summary>
        private static void Spin()
        {
            var reply = RouletteApi.Spin();

            if (reply == null)
            {
                SetStatus("No answer from the server.");
                return;
            }

            var error = (string)reply["Error"];
            if (!string.IsNullOrEmpty(error))
            {
                Render(reply);
                SetStatus(error);
                return;
            }

            var last = reply["Table"]?["Last"] as JObject;

            // Pressing spin on a settled table opens the next one, and that reply
            // carries the previous result rather than a new one. Nothing to animate.
            if (last == null || (int?)reply["Table"]?["Staked"] > 0)
            {
                Render(reply);
                return;
            }

            _lastReply = reply;

            var position = (int?)last["Position"] ?? 0;

            SetStatus("No more bets.");
            SetResult(string.Empty, null);
            BuildActions([]);

            WheelView.Spin(position, () =>
            {
                var label = (string)last["Label"] ?? "?";
                var colour = (string)last["Colour"];
                var profit = (int?)last["Profit"] ?? 0;

                SetResult(label, colour);

                SetStatus(profit >= 0
                    ? $"Up {profit:N0} on the spin."
                    : $"Down {Math.Abs(profit):N0} on the spin.");

                Render(_lastReply, keepStatus: true, keepResult: true);
            });
        }

        // ---------------------------------------------------------------- rendering

        private static void Render(JObject reply, bool keepStatus = false, bool keepResult = false)
        {
            var table = reply?["Table"] as JObject;

            if (table == null)
            {
                SetStatus("Not at a table.");
                BuildActions(Lobby());
                return;
            }

            _lastReply = reply;

            EnsureWheel(table["Pockets"] as JArray);

            var staked = (int?)table["Staked"] ?? 0;
            var phase = (string)table["Phase"] ?? "Betting";
            var bets = table["Bets"] as JArray;

            if (!keepStatus)
            {
                SetStatus(staked > 0
                    ? $"{staked:N0} on the cloth across {bets?.Count ?? 0} bet(s)."
                    : "Put something on the cloth.");
            }

            if (!keepResult)
            {
                var last = table["Last"] as JObject;

                if (last != null && string.Equals(phase, "Settled", StringComparison.OrdinalIgnoreCase))
                {
                    SetResult((string)last["Label"], (string)last["Colour"]);
                }
                else
                {
                    SetResult(string.Empty, null);
                }
            }

            BuildActions(Controls(phase, staked));
        }

        /// <summary>
        /// Builds the wheel the first time, and again only if the pockets changed.
        ///
        /// Keyed on the pocket list itself rather than on a wheel name: the list is
        /// what the wheel is drawn from, so anything that would change the drawing
        /// changes the key.
        /// </summary>
        private static void EnsureWheel(JArray pockets)
        {
            if (pockets == null || _wheelHolder == null)
            {
                return;
            }

            var signature = string.Join(",", pockets.Select(p => (string)p["Label"]));

            if (signature == _pocketSignature)
            {
                return;
            }

            for (var i = _wheelHolder.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_wheelHolder.GetChild(i).gameObject);
            }

            var built = pockets
                .Select(p => new PocketInfo(
                    (int?)p["Number"] ?? 0,
                    (string)p["Label"] ?? "?",
                    (string)p["Colour"] ?? "Black"))
                .ToList();

            WheelView.Build(_wheelHolder, built, WheelSize, _font);
            _pocketSignature = signature;
        }

        private static List<KeyValuePair<string, Action>> Lobby() =>
            [Action("OPEN A TABLE", () => Render(RouletteApi.State())), Action("CLOSE", Close)];

        /// <summary>
        /// A handful of bets and a spin. Not the cloth -- that is the next piece of
        /// work -- but enough to put money on several different rules at once and see
        /// them settle.
        /// </summary>
        private static List<KeyValuePair<string, Action>> Controls(string phase, int staked)
        {
            if (string.Equals(phase, "Settled", StringComparison.OrdinalIgnoreCase))
            {
                return [Action("NEXT SPIN", Spin), Action("CLOSE", Close)];
            }

            var controls = new List<KeyValuePair<string, Action>>
            {
                Action("RED", () => Place("Red", 0)),
                Action("BLACK", () => Place("Black", 0)),
                Action("ODD", () => Place("Odd", 0)),
                Action("EVEN", () => Place("Even", 0)),
                Action("1-18", () => Place("Low", 0)),
                Action("19-36", () => Place("High", 0)),
                Action("17", () => Place("Straight", 17)),
            };

            if (staked > 0)
            {
                controls.Add(Action("CLEAR", Clear));
                controls.Add(Action("SPIN", Spin));
            }

            controls.Add(Action("CLOSE", Close));

            return controls;
        }

        // ---------------------------------------------------------------- building

        private static void Build()
        {
            _font = BorrowFont();

            var canvasObject = new GameObject(
                RootName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Match height, not a blend, or an ultrawide stretches the wheel into an
            // ellipse -- which on a wheel is worse than on anything else.
            scaler.matchWidthOrHeight = 1f;

            _root = canvasObject;

            var backdrop = NewBox("Backdrop", canvasObject.transform, new Color(0f, 0f, 0f, 0.93f));
            Stretch(backdrop);

            var title = NewText("Title", canvasObject.transform, "ROULETTE", 30f, TextAlignmentOptions.Center);
            title.rectTransform.anchorMin = new Vector2(0.5f, 1f);
            title.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            title.rectTransform.pivot = new Vector2(0.5f, 1f);
            title.rectTransform.sizeDelta = new Vector2(600f, 44f);
            title.rectTransform.anchoredPosition = new Vector2(0f, -18f);
            title.color = Gold;

            _wheelHolder = NewBox("Wheel", canvasObject.transform, Color.clear);
            _wheelHolder.anchorMin = _wheelHolder.anchorMax = new Vector2(0.5f, 0.5f);
            _wheelHolder.pivot = new Vector2(0.5f, 0.5f);
            _wheelHolder.sizeDelta = new Vector2(WheelSize, WheelSize);
            _wheelHolder.anchoredPosition = new Vector2(0f, 66f);

            _result = NewText("Result", canvasObject.transform, string.Empty, 44f, TextAlignmentOptions.Center);
            _result.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _result.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _result.rectTransform.pivot = new Vector2(0.5f, 0f);
            _result.rectTransform.sizeDelta = new Vector2(900f, 56f);
            _result.rectTransform.anchoredPosition = new Vector2(0f, 168f);
            _result.color = Gold;

            _status = NewText("Status", canvasObject.transform, string.Empty, 19f, TextAlignmentOptions.Center);
            _status.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _status.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _status.rectTransform.pivot = new Vector2(0.5f, 0f);
            _status.rectTransform.sizeDelta = new Vector2(1400f, 30f);
            _status.rectTransform.anchoredPosition = new Vector2(0f, 124f);

            _actionRow = NewBox("Actions", canvasObject.transform, Color.clear);
            _actionRow.anchorMin = new Vector2(0.5f, 0f);
            _actionRow.anchorMax = new Vector2(0.5f, 0f);
            _actionRow.pivot = new Vector2(0.5f, 0f);
            _actionRow.sizeDelta = new Vector2(1700f, 52f);
            _actionRow.anchoredPosition = new Vector2(0f, 46f);

            var strip = _actionRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            strip.spacing = 10f;
            strip.childAlignment = TextAnchor.MiddleCenter;
            strip.childForceExpandWidth = false;
            strip.childForceExpandHeight = false;
            strip.childControlWidth = false;
            strip.childControlHeight = false;
        }

        private static void BuildActions(IEnumerable<KeyValuePair<string, Action>> actions)
        {
            if (_actionRow == null)
            {
                return;
            }

            for (var i = _actionRow.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_actionRow.GetChild(i).gameObject);
            }

            foreach (var action in actions)
            {
                BuildButton(_actionRow, action.Key, action.Value);
            }
        }

        private static void BuildButton(Transform parent, string label, Action onClick)
        {
            var box = NewBox("Button_" + label, parent, Color.white);
            box.sizeDelta = new Vector2(Mathf.Max(78f, 26f + (label.Length * 12f)), 44f);

            var image = box.GetComponent<Image>();
            image.sprite = Textures.ButtonFace(
                6,
                new Color(0.19f, 0.20f, 0.19f, 1f),
                new Color(0.11f, 0.12f, 0.11f, 1f),
                Gold);
            image.type = Image.Type.Sliced;

            var text = NewText("Label", box, label, 18f, TextAlignmentOptions.Center);
            Stretch(text.rectTransform);

            box.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());
        }

        private static KeyValuePair<string, Action> Action(string label, Action onClick) =>
            new KeyValuePair<string, Action>(label, onClick);

        private static void SetStatus(string text)
        {
            if (_status != null)
            {
                _status.text = text;
            }
        }

        /// <summary>
        /// The winning number, drawn in the colour it came up. A player looks here
        /// first and should not have to read a word to know how it went.
        /// </summary>
        private static void SetResult(string label, string colour)
        {
            if (_result == null)
            {
                return;
            }

            _result.text = string.IsNullOrEmpty(label) ? string.Empty : label;

            _result.color = colour switch
            {
                "Red" => new Color(0.85f, 0.27f, 0.25f, 1f),
                "Green" => new Color(0.35f, 0.78f, 0.50f, 1f),
                "Black" => new Color(0.86f, 0.86f, 0.88f, 1f),
                _ => Gold,
            };
        }

        private static RectTransform NewBox(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = colour;
            return (RectTransform)go.transform;
        }

        private static TextMeshProUGUI NewText(
            string name, Transform parent, string text, float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = alignment;
            label.color = Ink;
            label.raycastTarget = false;

            if (_font != null)
            {
                label.font = _font;
            }

            return label;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Borrows a font the game has already loaded rather than shipping one.
        /// TextMeshPro renders nothing at all with a null font, so a label that never
        /// appears looks like a layout bug rather than a missing asset.
        /// </summary>
        private static TMP_FontAsset BorrowFont()
        {
            try
            {
                return Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
            }
            catch (Exception ex)
            {
                RouletteClientPlugin.Log.LogWarning("[Roulette] could not borrow a font: " + ex.Message);
                return null;
            }
        }
    }
}
