using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Roulette.Client
{
    /// <summary>
    /// The betting cloth.
    ///
    /// ## The layout is the rules
    ///
    /// The grid is three rows of twelve running 1..36 **up the columns**, which is why
    /// the top row is 3, 6, 9 and the bottom row 1, 4, 7. That is not decoration: a
    /// street is a column of the printed grid, a corner is a square of four on it, and
    /// the three "2 to 1" boxes down the side are the *rows* of the print, which the
    /// rules call columns. Draw it any other way and every inside bet points at the
    /// wrong numbers.
    ///
    /// ## Line bets are spots, not cells
    ///
    /// A chip on a number is a straight-up bet; a chip on the line between two numbers
    /// is a split; on a corner where four meet, a corner bet. Those are real positions
    /// on a real cloth rather than extra buttons, so they are drawn where they belong
    /// -- small round targets sitting on the joins -- rather than as a separate list of
    /// controls.
    ///
    /// The engine already enumerates every legal one, and **a split is sent as an index
    /// into that list** because "the split on 1" is ambiguous between 1-2 and 1-4. This
    /// builds its targets from the same enumeration, so the cloth cannot offer a bet
    /// the server would refuse.
    /// </summary>
    internal static class ClothView
    {
        /// <summary>How wide one number cell is. Everything else is measured off it.</summary>
        private const float Cell = 66f;

        private const float Rows = 3f;
        private const float Columns = 12f;

        /// <summary>The dozen and outside rows below the grid.</summary>
        private const float OutsideRow = 46f;

        /// <summary>The round targets that sit on the joins between cells.</summary>
        private const float SpotSize = 19f;

        private static readonly Color Felt = new Color(0.055f, 0.24f, 0.145f, 1f);
        private static readonly Color FeltEdge = new Color(0.72f, 0.62f, 0.34f, 1f);
        private static readonly Color Red = new Color(0.62f, 0.11f, 0.13f, 1f);
        private static readonly Color Black = new Color(0.09f, 0.09f, 0.10f, 1f);
        private static readonly Color Green = new Color(0.09f, 0.40f, 0.22f, 1f);
        private static readonly Color Ink = new Color(0.93f, 0.91f, 0.86f, 1f);
        private static readonly Color Spot = new Color(0.85f, 0.78f, 0.55f, 0.22f);

        private static TMP_FontAsset _font;
        private static Action<string, int> _onBet;

        /// <summary>Every bet that has a place on the cloth, and where its chips go.</summary>
        private static readonly Dictionary<string, RectTransform> Stacks = new Dictionary<string, RectTransform>();

        internal static float Width => (Columns + 2f) * Cell;

        internal static float Height => (Rows * Cell) + (2f * OutsideRow);

        /// <summary>
        /// Builds the cloth. <paramref name="onBet"/> is handed the bet kind and its
        /// selection, exactly as the server names them.
        /// </summary>
        internal static GameObject Build(
            Transform parent, ClothLayout layout, TMP_FontAsset font, Action<string, int> onBet)
        {
            _font = font;
            _onBet = onBet;
            Stacks.Clear();

            var root = NewBox("Cloth", parent, Felt);
            root.sizeDelta = new Vector2(Width, Height);

            var backing = root.GetComponent<Image>();
            backing.sprite = Textures.RoundedBox(8, Felt, FeltEdge, 2);
            backing.type = Image.Type.Sliced;

            // Origin at the top left of the number grid, which is one cell in from the
            // left edge because the zero has that column to itself.
            var left = (-Width * 0.5f) + Cell;
            var top = Height * 0.5f;

            BuildZero(root, left, top);
            BuildNumbers(root, left, top);
            BuildColumnBets(root, left, top);
            BuildDozens(root, left, top);
            BuildOutside(root, left, top);
            BuildLineBets(root, layout, left, top);

            return root.gameObject;
        }

        /// <summary>
        /// Shows what is on the cloth. Rebuilt from the server's list every time rather
        /// than tracked here, so a refused bet or a cleared table cannot leave a chip
        /// behind that the server does not think exists.
        /// </summary>
        internal static void ShowBets(IEnumerable<(string Kind, int Selection, int Amount)> bets)
        {
            foreach (var stack in Stacks.Values)
            {
                for (var i = stack.childCount - 1; i >= 0; i--)
                {
                    UnityEngine.Object.Destroy(stack.GetChild(i).gameObject);
                }
            }

            if (bets == null)
            {
                return;
            }

            foreach (var bet in bets)
            {
                if (Stacks.TryGetValue(Key(bet.Kind, bet.Selection), out var stack))
                {
                    ChipView.Build(stack, bet.Amount, _font, size: 30f, maxChips: 3);
                }
            }
        }

        private static string Key(string kind, int selection) =>
            kind.ToLowerInvariant() + ":" + selection;

        // ---------------------------------------------------------------- the grid

        /// <summary>
        /// Where a number sits on the printed grid.
        ///
        /// The grid runs up the columns, so 1, 2, 3 share the first column with 3 at the
        /// top. Column is (n-1)/3 and the row is counted from the top, which is what
        /// turns a street -- three consecutive numbers -- into one printed column.
        /// </summary>
        private static Vector2 Place(int number, float left, float top)
        {
            var column = (number - 1) / 3;
            var row = 2 - ((number - 1) % 3);

            return new Vector2(
                left + (column * Cell) + (Cell * 0.5f),
                top - (row * Cell) - (Cell * 0.5f));
        }

        private static void BuildZero(RectTransform root, float left, float top)
        {
            var cell = NewCell(root, "0", Green, Cell, Rows * Cell);
            cell.anchoredPosition = new Vector2(left - (Cell * 0.5f), top - (Rows * Cell * 0.5f));
            Wire(cell, "Straight", 0);
        }

        private static void BuildNumbers(RectTransform root, float left, float top)
        {
            for (var n = 1; n <= 36; n++)
            {
                var red = IsRed(n);
                var cell = NewCell(root, n.ToString(), red ? Red : Black, Cell, Cell);
                cell.anchoredPosition = Place(n, left, top);
                Wire(cell, "Straight", n);
            }
        }

        /// <summary>
        /// The three "2 to 1" boxes down the right-hand side. They are the *rows* of the
        /// printed grid, which the rules call columns -- column 1 is 1, 4, 7 and so on,
        /// which prints along the bottom.
        /// </summary>
        private static void BuildColumnBets(RectTransform root, float left, float top)
        {
            for (var row = 0; row < 3; row++)
            {
                var column = 3 - row;
                var cell = NewCell(root, "2 to 1", Color.clear, Cell, Cell);
                cell.anchoredPosition = new Vector2(
                    left + (Columns * Cell) + (Cell * 0.5f),
                    top - (row * Cell) - (Cell * 0.5f));

                Wire(cell, "Column", column);
            }
        }

        private static void BuildDozens(RectTransform root, float left, float top)
        {
            var labels = new[] { "1st 12", "2nd 12", "3rd 12" };
            var width = 4f * Cell;

            for (var d = 0; d < 3; d++)
            {
                var cell = NewCell(root, labels[d], Color.clear, width, OutsideRow);
                cell.anchoredPosition = new Vector2(
                    left + (d * width) + (width * 0.5f),
                    top - (Rows * Cell) - (OutsideRow * 0.5f));

                Wire(cell, "Dozen", d + 1);
            }
        }

        private static void BuildOutside(RectTransform root, float left, float top)
        {
            var bets = new (string Label, string Kind, Color Tint)[]
            {
                ("1-18", "Low", Color.clear),
                ("EVEN", "Even", Color.clear),
                ("RED", "Red", Red),
                ("BLACK", "Black", Black),
                ("ODD", "Odd", Color.clear),
                ("19-36", "High", Color.clear),
            };

            var width = 2f * Cell;

            for (var i = 0; i < bets.Length; i++)
            {
                var cell = NewCell(root, bets[i].Label, bets[i].Tint, width, OutsideRow);
                cell.anchoredPosition = new Vector2(
                    left + (i * width) + (width * 0.5f),
                    top - (Rows * Cell) - OutsideRow - (OutsideRow * 0.5f));

                Wire(cell, bets[i].Kind, 0);
            }
        }

        /// <summary>
        /// Splits, streets, corners and six lines, as targets on the joins.
        ///
        /// Built from the engine's own enumeration rather than from a fresh reading of
        /// the grid, so what the cloth offers and what the server accepts are the same
        /// list. A split's selection is its index in that list.
        /// </summary>
        private static void BuildLineBets(RectTransform root, ClothLayout layout, float left, float top)
        {
            // Splits: on the join between the two numbers, which is their midpoint.
            for (var i = 0; i < layout.Splits.Count; i++)
            {
                var pair = layout.Splits[i];

                var a = pair.Low == 0
                    ? new Vector2(left - (Cell * 0.5f), Place(pair.High, left, top).y)
                    : Place(pair.Low, left, top);

                var b = Place(pair.High, left, top);

                AddSpot(root, (a + b) * 0.5f, "Split", i);
            }

            // Streets: off the bottom edge of each printed column.
            foreach (var street in layout.Streets)
            {
                var p = Place(street, left, top);
                AddSpot(root, new Vector2(p.x, p.y - (Cell * 0.5f)), "Street", street);
            }

            // Corners: the point where four cells meet, which is the corner of the
            // lowest of them.
            foreach (var corner in layout.Corners)
            {
                var p = Place(corner, left, top);
                AddSpot(root, new Vector2(p.x + (Cell * 0.5f), p.y + (Cell * 0.5f)), "Corner", corner);
            }

            // Six lines: on the bottom edge, between two columns.
            foreach (var line in layout.SixLines)
            {
                var p = Place(line, left, top);
                AddSpot(root, new Vector2(p.x + (Cell * 0.5f), p.y - (Cell * 0.5f)), "SixLine", line);
            }
        }

        private static void AddSpot(RectTransform root, Vector2 at, string kind, int selection)
        {
            var spot = NewBox("Spot_" + kind + selection, root, Spot);
            spot.sizeDelta = new Vector2(SpotSize, SpotSize);
            spot.anchoredPosition = at;

            var image = spot.GetComponent<Image>();
            image.sprite = Textures.RoundedBox(
                (int)(SpotSize * 0.5f), Spot, new Color(0.85f, 0.78f, 0.55f, 0.5f), 1);
            image.type = Image.Type.Sliced;
            image.raycastTarget = true;

            Wire(spot, kind, selection);
        }

        // ---------------------------------------------------------------- the pieces

        private static RectTransform NewCell(
            RectTransform root, string label, Color tint, float width, float height)
        {
            var cell = NewBox("Cell_" + label, root, tint == Color.clear ? new Color(0f, 0f, 0f, 0f) : tint);
            cell.sizeDelta = new Vector2(width - 3f, height - 3f);

            var image = cell.GetComponent<Image>();
            image.sprite = Textures.RoundedBox(
                4, tint == Color.clear ? new Color(1f, 1f, 1f, 0.04f) : tint, FeltEdge, 2);
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            image.raycastTarget = true;

            var text = NewText(cell, label, height > Cell ? 22f : (width > Cell * 1.5f ? 18f : 21f));
            Stretch(text.rectTransform);

            return cell;
        }

        /// <summary>
        /// Makes a cell take a chip, and gives it somewhere to show what is on it.
        ///
        /// The chip holder is a child of the cell rather than a separate layer, so a
        /// stack cannot drift away from the spot it belongs to.
        /// </summary>
        private static void Wire(RectTransform cell, string kind, int selection)
        {
            var button = cell.gameObject.AddComponent<Button>();
            var captured = kind;
            var chosen = selection;
            button.onClick.AddListener(() => _onBet?.Invoke(captured, chosen));

            var stack = NewBox("Chips", cell, new Color(0f, 0f, 0f, 0f));
            stack.anchorMin = stack.anchorMax = new Vector2(0.5f, 0.5f);
            stack.pivot = new Vector2(0.5f, 0.5f);
            stack.sizeDelta = new Vector2(Cell, Cell * 0.6f);
            stack.GetComponent<Image>().raycastTarget = false;

            var row = stack.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.childControlWidth = false;
            row.childControlHeight = false;

            Stacks[Key(kind, selection)] = stack;
        }

        private static bool IsRed(int n) =>
            n == 1 || n == 3 || n == 5 || n == 7 || n == 9 || n == 12 || n == 14 || n == 16
            || n == 18 || n == 19 || n == 21 || n == 23 || n == 25 || n == 27 || n == 30
            || n == 32 || n == 34 || n == 36;

        private static RectTransform NewBox(string name, Transform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            go.GetComponent<Image>().color = colour;

            return rect;
        }

        private static TextMeshProUGUI NewText(Transform parent, string text, float size)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Ink;
            label.raycastTarget = false;
            label.enableWordWrapping = false;

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
    }

    /// <summary>
    /// The cloth's spots, as the server described them.
    ///
    /// Read off the view rather than worked out here. A split is placed by its index in
    /// <see cref="Splits"/>, so a client that enumerated its own would be sending
    /// indices into a list nobody else has.
    /// </summary>
    internal sealed class ClothLayout
    {
        internal ClothLayout(
            IReadOnlyList<(int Low, int High)> splits,
            IReadOnlyList<int> streets,
            IReadOnlyList<int> corners,
            IReadOnlyList<int> sixLines)
        {
            Splits = splits;
            Streets = streets;
            Corners = corners;
            SixLines = sixLines;
        }

        internal IReadOnlyList<(int Low, int High)> Splits { get; }

        internal IReadOnlyList<int> Streets { get; }

        internal IReadOnlyList<int> Corners { get; }

        internal IReadOnlyList<int> SixLines { get; }
    }
}
