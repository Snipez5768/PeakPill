using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PeakPill.UI;

/// <summary>
/// Own vector icon set drawn in the style of Apple's SF Symbols (24-unit grid, round caps and joins).
/// Not Apple's artwork – SF Symbols may only be used on Apple platforms.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, (Geometry Stroke, Geometry Fill)> Map = new();

    static Icons()
    {
        // ---- navigation ----
        S("music", "M9,18 V5.5 L20,3.5 V16", F: Circles((6.5, 18, 2.6), (17.5, 16, 2.6)));
        S("clipboard", "M8.5,4.5 H7 A2,2 0 0 0 5,6.5 V19.5 A2,2 0 0 0 7,21.5 H17 A2,2 0 0 0 19,19.5 V6.5 A2,2 0 0 0 17,4.5 H15.5 M9.5,2.5 H14.5 A1,1 0 0 1 15.5,3.5 V5.5 A1,1 0 0 1 14.5,6.5 H9.5 A1,1 0 0 1 8.5,5.5 V3.5 A1,1 0 0 1 9.5,2.5 Z M9,11 H15 M9,15 H13");
        S("folder", "M3,7.5 A2,2 0 0 1 5,5.5 H9 L11,7.5 H19 A2,2 0 0 1 21,9.5 V17.5 A2,2 0 0 1 19,19.5 H5 A2,2 0 0 1 3,17.5 Z M3,10 H21");
        S("widgets", "M5.5,4 H9 A1.5,1.5 0 0 1 10.5,5.5 V9 A1.5,1.5 0 0 1 9,10.5 H5.5 A1.5,1.5 0 0 1 4,9 V5.5 A1.5,1.5 0 0 1 5.5,4 Z M15,4 H18.5 A1.5,1.5 0 0 1 20,5.5 V9 A1.5,1.5 0 0 1 18.5,10.5 H15 A1.5,1.5 0 0 1 13.5,9 V5.5 A1.5,1.5 0 0 1 15,4 Z M5.5,13.5 H9 A1.5,1.5 0 0 1 10.5,15 V18.5 A1.5,1.5 0 0 1 9,20 H5.5 A1.5,1.5 0 0 1 4,18.5 V15 A1.5,1.5 0 0 1 5.5,13.5 Z M15,13.5 H18.5 A1.5,1.5 0 0 1 20,15 V18.5 A1.5,1.5 0 0 1 18.5,20 H15 A1.5,1.5 0 0 1 13.5,18.5 V15 A1.5,1.5 0 0 1 15,13.5 Z");
        S("bell", "M6,16.5 V11 A6,6 0 0 1 18,11 V16.5 L19.5,18 H4.5 Z M10,20.5 A2.2,2.2 0 0 0 14,20.5");
        S("sliders", "M4,7 H13 M17,7 H20 M4,17 H7 M11,17 H20", F: null, extra: Circles((15, 7, 2.2), (9, 17, 2.2)));
        S("gear", Gear());
        S("search", "M10.5,4 A6.5,6.5 0 1 1 10.49,4 Z M15.3,15.3 L20,20");

        // ---- actions ----
        S("trash", "M4,6.5 H20 M9.5,6.5 V4.5 A1,1 0 0 1 10.5,3.5 H13.5 A1,1 0 0 1 14.5,4.5 V6.5 M6,6.5 L7,19.5 A1.8,1.8 0 0 0 8.8,21 H15.2 A1.8,1.8 0 0 0 17,19.5 L18,6.5 M10,10.5 V17 M14,10.5 V17");
        S("pencil", "M4,20 L4.8,15.6 L15.8,4.6 A1.5,1.5 0 0 1 17.9,4.6 L19.4,6.1 A1.5,1.5 0 0 1 19.4,8.2 L8.4,19.2 Z M14,6.5 L17.5,10");
        S("share", "M12,3 V14.5 M8,7 L12,3 L16,7 M8.5,10 H7 A2,2 0 0 0 5,12 V19 A2,2 0 0 0 7,21 H17 A2,2 0 0 0 19,19 V12 A2,2 0 0 0 17,10 H15.5");
        S("copy", "M9,8 H18 A2,2 0 0 1 20,10 V19 A2,2 0 0 1 18,21 H11 A2,2 0 0 1 9,19 Z M15.5,8 V6 A2,2 0 0 0 13.5,4 H6 A2,2 0 0 0 4,6 V13.5 A2,2 0 0 0 6,15.5 H9");
        S("check", "M5,12.5 L10,17.5 L19,7");
        S("xmark", "M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5");
        S("plus", "M12,5 V19 M5,12 H19");
        S("minus", "M5,12 H19");
        S("pin", "M14.5,3.5 L20.5,9.5 L17.5,10.5 L14,14 L14.5,18 L12.5,20 L4,11.5 L6,9.5 L10,10 L13.5,6.5 Z M8.2,15.8 L3.5,20.5");
        S("save", "M12,3.5 V14.5 M7.5,10 L12,14.5 L16.5,10 M4.5,15 V18.5 A2.5,2.5 0 0 0 7,21 H17 A2.5,2.5 0 0 0 19.5,18.5 V15");
        S("download", "M12,4 V15.5 M7,10.5 L12,15.5 L17,10.5 M5,20 H19");
        S("refresh", "M19.5,12 A7.5,7.5 0 1 1 17.3,6.7 M18,2.5 V7 H13.5");
        S("swap", "M4.5,12 A7.5,7.5 0 0 1 17.5,7 M17.5,3 V7 H13.5 M19.5,12 A7.5,7.5 0 0 1 6.5,17 M6.5,21 V17 H10.5");
        S("ellipsis", "", F: Circles((6, 12, 1.6), (12, 12, 1.6), (18, 12, 1.6)));
        S("chevron.down", "M7,10 L12,15 L17,10");
        S("chevron.up", "M7,14 L12,9 L17,14");
        S("chevron.left", "M14.5,6 L8.5,12 L14.5,18");
        S("chevron.right", "M9.5,6 L15.5,12 L9.5,18");
        S("undo", "M9,14 L4.5,9.5 L9,5 M4.5,9.5 H14.5 A5,5 0 0 1 14.5,19.5 H11");
        S("redo", "M15,14 L19.5,9.5 L15,5 M19.5,9.5 H9.5 A5,5 0 0 0 9.5,19.5 H13");
        S("open", "M14,4 H20 V10 M20,4 L11,13 M18,14 V18.5 A1.5,1.5 0 0 1 16.5,20 H5.5 A1.5,1.5 0 0 1 4,18.5 V7.5 A1.5,1.5 0 0 1 5.5,6 H10");

        // ---- content types ----
        S("link", "M10.2,13.8 L13.8,10.2 M8.6,9.6 L6.9,11.3 A3.8,3.8 0 0 0 12.3,16.7 L14,15 M10,9 L11.7,7.3 A3.8,3.8 0 0 1 17.1,12.7 L15.4,14.4");
        S("doc", "M7,3 H13.5 L19,8.5 V19 A2,2 0 0 1 17,21 H7 A2,2 0 0 1 5,19 V5 A2,2 0 0 1 7,3 Z M13.5,3 V8.5 H19 M8.5,13 H15.5 M8.5,16.5 H13.5");
        S("envelope", "M5,5.5 H19 A2,2 0 0 1 21,7.5 V16.5 A2,2 0 0 1 19,18.5 H5 A2,2 0 0 1 3,16.5 V7.5 A2,2 0 0 1 5,5.5 Z M3.6,7 L12,13 L20.4,7");
        S("photo","M5,4.5 H19 A2,2 0 0 1 21,6.5 V17.5 A2,2 0 0 1 19,19.5 H5 A2,2 0 0 1 3,17.5 V6.5 A2,2 0 0 1 5,4.5 Z M3.5,16 L8.5,11 L13,15.5 L15.5,13 L20.5,18", F: Circles((15.5, 8.8, 1.6)));
        S("text.viewfinder", "M4,8.5 V6 A2,2 0 0 1 6,4 H8.5 M15.5,4 H18 A2,2 0 0 1 20,6 V8.5 M20,15.5 V18 A2,2 0 0 1 18,20 H15.5 M8.5,20 H6 A2,2 0 0 1 4,18 V15.5 M8,9 H16 M8,12 H16 M8,15 H12.5");
        S("qr", "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H16 M14,14 V16 M18,14 H20 V16 M14,20 H16 M18,18 H20 V20", F: Rects((6, 6, 2, 2), (16, 6, 2, 2), (6, 16, 2, 2), (16.2, 16.2, 1.6, 1.6)));
        S("iphone", "M8,2.5 H16 A2,2 0 0 1 18,4.5 V19.5 A2,2 0 0 1 16,21.5 H8 A2,2 0 0 1 6,19.5 V4.5 A2,2 0 0 1 8,2.5 Z M10.5,5.5 H13.5");

        // ---- system ----
        S("mic", "M12,3 A3,3 0 0 1 15,6 V11.5 A3,3 0 0 1 9,11.5 V6 A3,3 0 0 1 12,3 Z M6,11 A6,6 0 0 0 18,11 M12,17 V21 M9,21 H15");
        S("mic.slash", "M12,3 A3,3 0 0 1 15,6 V11.5 A3,3 0 0 1 9,11.5 V6 A3,3 0 0 1 12,3 Z M6,11 A6,6 0 0 0 18,11 M12,17 V21 M9,21 H15 M4,4 L20,20");
        S("waveform", "M3.5,10.5 V13.5 M7.5,7.5 V16.5 M11.5,4 V20 M15.5,8 V16 M19.5,10 V14"); // 22: which song is this
        S("timer", "M12,5.5 A7.75,7.75 0 1 1 11.99,5.5 Z M12,13.25 V9.5 M9.5,2.5 H14.5 M18.2,6.6 L19.6,5.2");
        S("bell.badge", "M6,16.5 V11 A6,6 0 0 1 14.2,5.4 M18,10.6 V16.5 L19.5,18 H4.5 L6,16.5 M10,20.5 A2.2,2.2 0 0 0 14,20.5", F: Circles((18.2, 5.8, 2.6)));
        S("checklist", "M4,6 L5.5,7.5 L8,5 M4,12 L5.5,13.5 L8,11 M4,18 L5.5,19.5 L8,17 M11,6.3 H20 M11,12.3 H20 M11,18.3 H20");
        S("cart", "M3,4 H5.5 L8,15.5 H18 L20,8 H6.6 M9.5,19.5 A1,1 0 1 1 9.49,19.5 Z M16.5,19.5 A1,1 0 1 1 16.49,19.5 Z");
        S("bitcoin", "M12,3 A9,9 0 1 1 11.99,3 Z M9.5,7.5 H13 A2.2,2.2 0 0 1 13,11.9 H9.5 Z M9.5,11.9 H13.6 A2.3,2.3 0 0 1 13.6,16.5 H9.5 Z M9.5,7.5 V16.5 M11,6 V7.5 M11,16.5 V18 M13,6 V7.5 M13,16.5 V18");
        S("chart", "M4,4 V20 H20 M7,15 L11,10.5 L14,13 L19.5,7 M16.5,7 H19.5 V10");
        S("gamecontroller", "M7.5,7.5 H16.5 A4.5,4.5 0 0 1 21,12 L21.5,15.5 A2.4,2.4 0 0 1 17.3,17.3 L15.5,15 H8.5 L6.7,17.3 A2.4,2.4 0 0 1 2.5,15.5 L3,12 A4.5,4.5 0 0 1 7.5,7.5 Z M7.5,10 V13 M6,11.5 H9", F: Circles((15.5, 10.6, 0.9), (17.5, 12.6, 0.9)));
        S("gift", "M4,9 H20 V12.5 H4 Z M5.5,12.5 V20 H18.5 V12.5 M12,9 V20 M12,9 C10,5 6.5,5.5 7.5,7.6 C8.2,9 12,9 12,9 C14,5 17.5,5.5 16.5,7.6 C15.8,9 12,9 12,9");
        // AI agent: a four-point spark plus a small one
        S("sparkles", "M10,4 C10.6,8.6 12.4,10.4 17,11 C12.4,11.6 10.6,13.4 10,18 C9.4,13.4 7.6,11.6 3,11 C7.6,10.4 9.4,8.6 10,4 Z M18,3 C18.25,4.6 18.9,5.25 20.5,5.5 C18.9,5.75 18.25,6.4 18,8 C17.75,6.4 17.1,5.75 15.5,5.5 C17.1,5.25 17.75,4.6 18,3 Z");
        // AI agent in the bubble: an eight-ray star (generic asterisk, not a brand logo)
        S("asterisk", "M12,3.5 V20.5 M3.5,12 H20.5 M6,6 L18,18 M18,6 L6,18");
        // sharing the screen: a display with an arrow going out
        S("screen.share", "M4.5,4 H19.5 A1.5,1.5 0 0 1 21,5.5 V15.5 A1.5,1.5 0 0 1 19.5,17 H4.5 A1.5,1.5 0 0 1 3,15.5 V5.5 A1.5,1.5 0 0 1 4.5,4 Z M9,21 H15 M12,13.5 V7.5 M9.5,10 L12,7.5 L14.5,10");
        // in a call (approved 2026-11-14: green phone in the bubble)
        S("phone", "M6.6,3.5 H9 L10.5,7.5 L8.6,9 A11,11 0 0 0 15,15.4 L16.5,13.5 L20.5,15 V17.4 A2,2 0 0 1 18.4,19.5 A15.5,15.5 0 0 1 4.5,5.6 A2,2 0 0 1 6.6,3.5 Z");
        // hang up: the same handset turned down (2026-11-17)
        var down = Geometry.Parse("M6.6,3.5 H9 L10.5,7.5 L8.6,9 A11,11 0 0 0 15,15.4 L16.5,13.5 L20.5,15 V17.4 A2,2 0 0 1 18.4,19.5 A15.5,15.5 0 0 1 4.5,5.6 A2,2 0 0 1 6.6,3.5 Z").Clone();
        down.Transform = new RotateTransform(135, 12, 12.6);
        S("phone.down", down);
        S("arrow.up.right", "M7,17 L17,7 M9,7 H17 V15");
        S("shield", "M12,3 L19.5,6 V11.5 C19.5,16 16.3,19.6 12,21 C7.7,19.6 4.5,16 4.5,11.5 V6 Z M9.5,12 L11.3,13.8 L14.8,10.2");
        S("code","M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M13.5,5 L10.5,19");
        S("eye", "M2.5,12 C5,7 8.5,5 12,5 C15.5,5 19,7 21.5,12 C19,17 15.5,19 12,19 C8.5,19 5,17 2.5,12 Z M12,9 A3,3 0 1 1 11.99,9 Z");
        S("arrow.down", "M12,5 V18.5 M7,13.5 L12,18.5 L17,13.5");
        S("bell.ring", "M6,16.5 V11 A6,6 0 0 1 18,11 V16.5 L19.5,18 H4.5 Z M10,20.5 A2.2,2.2 0 0 0 14,20.5 M3,8.5 A9,9 0 0 1 5.5,4 M21,8.5 A9,9 0 0 0 18.5,4");
        // queue mode switched on: the front card filled
        S("stack.fill", "M7,6 H17 M9,3 H15", F: RoundRect(5, 9, 14, 12, 2));
        S("stack", "M7,9 H17 A2,2 0 0 1 19,11 V19 A2,2 0 0 1 17,21 H7 A2,2 0 0 1 5,19 V11 A2,2 0 0 1 7,9 Z M7,6 H17 M9,3 H15");
        S("wifi", "M2.5,9 A14,14 0 0 1 21.5,9 M5.5,12.5 A9.5,9.5 0 0 1 18.5,12.5 M8.7,16 A4.8,4.8 0 0 1 15.3,16", F: Circles((12, 19.3, 1.5)));
        S("bluetooth", "M7,7.5 L17,16.5 L12,21 V3 L17,7.5 L7,16.5");
        S("moon", "M19.5,14.5 A8,8 0 1 1 9.5,4.5 A6.5,6.5 0 0 0 19.5,14.5 Z");
        // 2026-11-18: rain, snow, a trophy, a glass of water
        S("drop", "M12,3.5 C9,8 6.5,11 6.5,14.5 A5.5,5.5 0 0 0 17.5,14.5 C17.5,11 15,8 12,3.5 Z");
        S("snow", "M12,3 V21 M4.2,7.5 L19.8,16.5 M4.2,16.5 L19.8,7.5 M10,4.5 L12,6.5 L14,4.5 M10,19.5 L12,17.5 L14,19.5");
        S("trophy", "M7.5,4 H16.5 V9.5 A4.5,4.5 0 0 1 7.5,9.5 Z M7.5,6 H4.5 V7.5 A3,3 0 0 0 7.5,10.5 M16.5,6 H19.5 V7.5 A3,3 0 0 1 16.5,10.5 M12,14 V18 M8.5,20.5 H15.5");
        S("glass", "M6,4 H18 L16.5,20 A1,1 0 0 1 15.5,21 H8.5 A1,1 0 0 1 7.5,20 Z M7,10 H17");
        S("sun", "M12,8 A4,4 0 1 1 11.99,8 Z M12,2.5 V4.5 M12,19.5 V21.5 M2.5,12 H4.5 M19.5,12 H21.5 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M5.3,18.7 L6.7,17.3 M17.3,6.7 L18.7,5.3");
        S("lock", "M6.5,11 H17.5 A1.5,1.5 0 0 1 19,12.5 V19.5 A1.5,1.5 0 0 1 17.5,21 H6.5 A1.5,1.5 0 0 1 5,19.5 V12.5 A1.5,1.5 0 0 1 6.5,11 Z M8,11 V7.5 A4,4 0 0 1 16,7.5 V11");
        S("power", "M12,3 V11.5 M7,5.8 A8,8 0 1 0 17,5.8");
        S("bolt", "M13,2.5 L4.5,13.5 H11 L10,21.5 L19.5,10 H13 Z");
        S("sport", "M12,3 A9,9 0 1 1 11.99,3 Z M12,8 L15.8,10.8 L14.4,15.2 H9.6 L8.2,10.8 Z M12,3 V8 M15.8,10.8 L20.5,9.3 M14.4,15.2 L17.3,19.3 M9.6,15.2 L6.7,19.3 M8.2,10.8 L3.5,9.3");
        S("heart", "M12,19.5 C12,19.5 3.5,14.6 3.5,8.9 A4.4,4.4 0 0 1 12,7.1 A4.4,4.4 0 0 1 20.5,8.9 C20.5,14.6 12,19.5 12,19.5 Z");
        S("heart.fill", "", F: Geometry.Parse("M12,19.5 C12,19.5 3.5,14.6 3.5,8.9 A4.4,4.4 0 0 1 12,7.1 A4.4,4.4 0 0 1 20.5,8.9 C20.5,14.6 12,19.5 12,19.5 Z"));
        // KI page (2026-11-17): a speech bubble with a small spark inside, "new chat" and "send"
        S("bubble", "M12,4 C16.97,4 21,7.36 21,11.5 C21,15.64 16.97,19 12,19 C10.9,19 9.85,18.84 8.88,18.54 L4.5,20 L5.6,16.4 C4,15.06 3,13.37 3,11.5 C3,7.36 7.03,4 12,4 Z",
          F: Geometry.Parse("M12,7.6 C12.3,9.9 13.1,10.7 15.4,11 C13.1,11.3 12.3,12.1 12,14.4 C11.7,12.1 10.9,11.3 8.6,11 C10.9,10.7 11.7,9.9 12,7.6 Z"));
        S("square.pencil", "M11,4.5 H6.5 A2,2 0 0 0 4.5,6.5 V17.5 A2,2 0 0 0 6.5,19.5 H17.5 A2,2 0 0 0 19.5,17.5 V13 M17.3,4.2 A1.75,1.75 0 0 1 19.8,6.7 L12.2,14.3 L9,15 L9.7,11.8 Z");
        S("arrow.up", "M12,19 V5.5 M6.5,11 L12,5.5 L17.5,11");
        S("quote","M5,7 H19 M5,11 H15 M5,15 H17 M5,19 H11");
        S("send", "M12,19 V5 M6,11 L12,5 L18,11");
        S("reply", "M10,8 L5,12 L10,16 M5,12 H15 A4,4 0 0 1 19,16 V18");
        S("calendar", "M5,6.5 H19 A2,2 0 0 1 21,8.5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V8.5 A2,2 0 0 1 5,6.5 Z M3,11 H21 M8,4 V8 M16,4 V8");
        S("arrow.left.right", "M4,12 H20 M8,8 L4,12 L8,16 M16,8 L20,12 L16,16");
        S("headphones", "M4,15 V12 A8,8 0 0 1 20,12 V15 M4,14.5 H6.5 A1.5,1.5 0 0 1 8,16 V19 A1.5,1.5 0 0 1 6.5,20.5 H5.5 A1.5,1.5 0 0 1 4,19 Z M20,14.5 H17.5 A1.5,1.5 0 0 0 16,16 V19 A1.5,1.5 0 0 0 17.5,20.5 H18.5 A1.5,1.5 0 0 0 20,19 Z");
        S("speaker", "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9 A4.2,4.2 0 0 1 15.5,15 M18.2,6.3 A8,8 0 0 1 18.2,17.7");
        S("speaker.slash", "M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9.5 L20.5,14.5 M20.5,9.5 L15.5,14.5");
        S("camera", "M4,8.5 A2,2 0 0 1 6,6.5 H8 L9.5,4.5 H14.5 L16,6.5 H18 A2,2 0 0 1 20,8.5 V17.5 A2,2 0 0 1 18,19.5 H6 A2,2 0 0 1 4,17.5 Z M12,9.5 A3.5,3.5 0 1 1 11.99,9.5 Z");
        S("keyboard", "M4.5,6 H19.5 A1.5,1.5 0 0 1 21,7.5 V16.5 A1.5,1.5 0 0 1 19.5,18 H4.5 A1.5,1.5 0 0 1 3,16.5 V7.5 A1.5,1.5 0 0 1 4.5,6 Z M7,14.5 H17 M7,10 H7.01 M10,10 H10.01 M13,10 H13.01 M16,10 H16.01");
        S("paintbrush", "M19.5,3.5 L20.5,4.5 L12,14 L10,12 Z M10,12 C7,12 6,14 6,16 C6,18 5,19.5 3.5,19.5 C6.5,21.5 12,21 12,16 Z");
        S("info", "M12,3 A9,9 0 1 1 11.99,3 Z M12,11 V16.5 M12,7.8 V7.9");
        S("clock", "M12,3 A9,9 0 1 1 11.99,3 Z M12,7 V12 L15.5,14");
        S("globe", "M12,3 A9,9 0 1 1 11.99,3 Z M3,12 H21 M12,3 C9,6 9,18 12,21 C15,18 15,6 12,3 Z");
        S("battery", "M4.5,7.5 H17 A1.5,1.5 0 0 1 18.5,9 V15 A1.5,1.5 0 0 1 17,16.5 H4.5 A1.5,1.5 0 0 1 3,15 V9 A1.5,1.5 0 0 1 4.5,7.5 Z M21,10.5 V13.5");
        S("cpu", "M7.5,6 H16.5 A1.5,1.5 0 0 1 18,7.5 V16.5 A1.5,1.5 0 0 1 16.5,18 H7.5 A1.5,1.5 0 0 1 6,16.5 V7.5 A1.5,1.5 0 0 1 7.5,6 Z M9,3 V6 M12,3 V6 M15,3 V6 M9,18 V21 M12,18 V21 M15,18 V21 M3,9 H6 M3,12 H6 M3,15 H6 M18,9 H21 M18,12 H21 M18,15 H21");

        // ---- screenshot / markup ----
        S("selection", "M4,8 V5 A1,1 0 0 1 5,4 H8 M11,4 H13 M16,4 H19 A1,1 0 0 1 20,5 V8 M20,11 V13 M20,16 V19 A1,1 0 0 1 19,20 H16 M13,20 H11 M8,20 H5 A1,1 0 0 1 4,19 V16 M4,13 V11");
        S("window", "M5,4.5 H19 A2,2 0 0 1 21,6.5 V17.5 A2,2 0 0 1 19,19.5 H5 A2,2 0 0 1 3,17.5 V6.5 A2,2 0 0 1 5,4.5 Z M3,8.5 H21", F: Circles((5.8, 6.5, 0.8), (8.3, 6.5, 0.8)));
        S("display", "M4.5,4 H19.5 A1.5,1.5 0 0 1 21,5.5 V15.5 A1.5,1.5 0 0 1 19.5,17 H4.5 A1.5,1.5 0 0 1 3,15.5 V5.5 A1.5,1.5 0 0 1 4.5,4 Z M9,21 H15 M12,17 V21");
        S("lasso", "M6,15 C2,11 5,4 13,4 C20,4 22,10 17,13.5 C13.5,16 8,16 6,15 Z M6,15 C5,18 7.5,19.5 9,18 C10,17 8.5,15.5 7,17 C6,18 6,20.5 7,21.5");
        S("record", "M12,3 A9,9 0 1 1 11.99,3 Z", F: Circles((12, 12, 4.5)));
        S("dot.radiowaves", "M8.6,8.6 A4.8,4.8 0 0 0 8.6,15.4 M15.4,8.6 A4.8,4.8 0 0 1 15.4,15.4 M5.6,5.6 A9,9 0 0 0 5.6,18.4 M18.4,5.6 A9,9 0 0 1 18.4,18.4", F: Circles((12, 12, 2.2)));
        S("stop", "", F: RoundRect(6, 6, 12, 12, 2.5));
        S("eyedropper", "M14.5,4.5 L19.5,9.5 M17,7 L18.6,5.4 A1.8,1.8 0 0 1 21.1,7.9 L19.5,9.5 M16,8 L7,17 L5,17.5 L4,20 L6.5,19 L7,17 M16,8 L13,5 M13,11 L10,8");
        S("highlighter", "M15,4 L20,9 L11,18 L7,18.5 L6,17.5 L6.5,13.5 Z M4,21 H10 M13,6 L18,11");
        S("arrow", "M5,19 L18.5,5.5 M10,5.5 H18.5 V14");
        S("rect", "M5.5,5 H18.5 A1.5,1.5 0 0 1 20,6.5 V17.5 A1.5,1.5 0 0 1 18.5,19 H5.5 A1.5,1.5 0 0 1 4,17.5 V6.5 A1.5,1.5 0 0 1 5.5,5 Z");
        S("ellipse", "M12,5 A8.5,7 0 1 1 11.99,5 Z");
        S("text", "M5,6.5 V4.5 H19 V6.5 M12,4.5 V19.5 M9,19.5 H15");
        S("pixelate", "", F: Rects((4, 4, 4.5, 4.5), (9.75, 4, 4.5, 4.5), (15.5, 4, 4.5, 4.5), (4, 9.75, 4.5, 4.5), (15.5, 9.75, 4.5, 4.5), (4, 15.5, 4.5, 4.5), (9.75, 15.5, 4.5, 4.5), (15.5, 15.5, 4.5, 4.5)));
        S("crop", "M7,3 V15.5 A1.5,1.5 0 0 0 8.5,17 H21 M3,7 H15.5 A1.5,1.5 0 0 1 17,8.5 V21");
        S("cursor", "M5.5,3.5 L18.5,11.5 L12.5,13 L9.5,19 Z");
        // edit mode in the big view: rotate / mirror / keep proportions
        S("rotate.left", "M10.5,11 H18.5 A1.5,1.5 0 0 1 20,12.5 V19 A1.5,1.5 0 0 1 18.5,20.5 H10.5 A1.5,1.5 0 0 1 9,19 V12.5 A1.5,1.5 0 0 1 10.5,11 Z M14,4 H9 A4.5,4.5 0 0 0 4.5,8.5 V13 M2,10.5 L4.5,13 L7,10.5");
        S("rotate.right", "M5.5,11 H13.5 A1.5,1.5 0 0 1 15,12.5 V19 A1.5,1.5 0 0 1 13.5,20.5 H5.5 A1.5,1.5 0 0 1 4,19 V12.5 A1.5,1.5 0 0 1 5.5,11 Z M10,4 H15 A4.5,4.5 0 0 1 19.5,8.5 V13 M17,10.5 L19.5,13 L22,10.5");
        S("flip.h", "M12,3 V21 M9,6 L3.5,18 H9 Z M15,6 L20.5,18 H15 Z");
        S("flip.v", "M3,12 H21 M6,9 L18,3.5 V9 Z M6,15 L18,20.5 V15 Z");
        S("link", "M10,14 L14,10 M8.5,11 L6.5,13 A3.2,3.2 0 0 0 11,17.5 L13,15.5 M15.5,13 L17.5,11 A3.2,3.2 0 0 0 13,6.5 L11,8.5");
        S("link.off", "M8.5,11 L6.5,13 A3.2,3.2 0 0 0 11,17.5 L13,15.5 M15.5,13 L17.5,11 A3.2,3.2 0 0 0 13,6.5 L11,8.5 M5,5 L19,19");
        S("forward.fill", "", F: Shifted("M0,2 C0,1.2 0.9,0.8 1.5,1.2 L9,6.6 C9.6,7 9.6,8 9,8.4 L1.5,13.8 C0.9,14.2 0,13.8 0,13 Z M10,2 C10,1.2 10.9,0.8 11.5,1.2 L19,6.6 C19.6,7 19.6,8 19,8.4 L11.5,13.8 C10.9,14.2 10,13.8 10,13 Z", 2, 4.5));
        S("backward.fill", "", F: Shifted("M20,2 C20,1.2 19.1,0.8 18.5,1.2 L11,6.6 C10.4,7 10.4,8 11,8.4 L18.5,13.8 C19.1,14.2 20,13.8 20,13 Z M10,2 C10,1.2 9.1,0.8 8.5,1.2 L1,6.6 C0.4,7 0.4,8 1,8.4 L8.5,13.8 C9.1,14.2 10,13.8 10,13 Z", 2, 4.5));
        S("play.fill", "", F: Geometry.Parse("M7,4.8 C7,4 7.9,3.5 8.6,3.9 L19.2,10.6 C19.9,11 19.9,12 19.2,12.4 L8.6,19.1 C7.9,19.5 7,19 7,18.2 Z"));
        S("pause.fill", "", F: Geometry.Parse("M6,5.2 A1.2,1.2 0 0 1 7.2,4 H9.3 A1.2,1.2 0 0 1 10.5,5.2 V18.8 A1.2,1.2 0 0 1 9.3,20 H7.2 A1.2,1.2 0 0 1 6,18.8 Z M13.5,5.2 A1.2,1.2 0 0 1 14.7,4 H16.8 A1.2,1.2 0 0 1 18,5.2 V18.8 A1.2,1.2 0 0 1 16.8,20 H14.7 A1.2,1.2 0 0 1 13.5,18.8 Z"));
    }

    private static void S(string name, string stroke, Geometry F = null, Geometry extra = null)
    {
        Geometry s = string.IsNullOrEmpty(stroke) ? null : Geometry.Parse(stroke);
        if (extra != null)
        {
            var g = new GeometryGroup();
            if (s != null) g.Children.Add(s);
            g.Children.Add(extra);
            s = g;
        }
        s?.Freeze();
        F?.Freeze();
        Map[name] = (s, F);
    }

    private static void S(string name, Geometry stroke)
    {
        stroke.Freeze();
        Map[name] = (stroke, null);
    }

    private static Geometry Circles(params (double X, double Y, double R)[] cs)
    {
        var g = new GeometryGroup();
        foreach (var (x, y, r) in cs) g.Children.Add(new EllipseGeometry(new Point(x, y), r, r));
        return g;
    }

    private static Geometry Rects(params (double X, double Y, double W, double H)[] rs)
    {
        var g = new GeometryGroup();
        foreach (var (x, y, w, h) in rs) g.Children.Add(new RectangleGeometry(new Rect(x, y, w, h), 0.8, 0.8));
        return g;
    }

    private static Geometry Shifted(string data, double dx, double dy)
    {
        var g = Geometry.Parse(data).Clone();
        g.Transform = new TranslateTransform(dx, dy);
        return g;
    }

    private static Geometry RoundRect(double x, double y, double w, double h, double r) => new RectangleGeometry(new Rect(x, y, w, h), r, r);

    private static Geometry Gear()
    {
        var sg = new StreamGeometry();
        using (var c = sg.Open())
        {
            const int teeth = 8;
            const double rOut = 9.3, rIn = 7.2, cx = 12, cy = 12;
            for (int i = 0; i < teeth; i++)
            {
                double a0 = (i - 0.22) * 2 * Math.PI / teeth, a1 = (i + 0.22) * 2 * Math.PI / teeth;
                double b0 = (i + 0.36) * 2 * Math.PI / teeth, b1 = (i + 0.64) * 2 * Math.PI / teeth;
                var p0 = new Point(cx + rOut * Math.Cos(a0), cy + rOut * Math.Sin(a0));
                var p1 = new Point(cx + rOut * Math.Cos(a1), cy + rOut * Math.Sin(a1));
                var q0 = new Point(cx + rIn * Math.Cos(b0), cy + rIn * Math.Sin(b0));
                var q1 = new Point(cx + rIn * Math.Cos(b1), cy + rIn * Math.Sin(b1));
                if (i == 0) c.BeginFigure(p0, false, true);
                else c.LineTo(p0, true, true);
                c.ArcTo(p1, new Size(rOut, rOut), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(q0, true, true);
                c.ArcTo(q1, new Size(rIn, rIn), 0, false, SweepDirection.Clockwise, true, true);
            }
        }
        var g = new GeometryGroup();
        g.Children.Add(sg);
        g.Children.Add(new EllipseGeometry(new Point(12, 12), 3, 3));
        return g;
    }

    public static (Geometry Stroke, Geometry Fill) Get(string name) =>
        name != null && Map.TryGetValue(name, out var v) ? v : (null, null);
}

/// <summary>Renders an icon from <see cref="Icons"/>. Colour follows the inherited Foreground.</summary>
public sealed class SfIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(SfIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(SfIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(nameof(Weight), typeof(double), typeof(SfIcon),
        new FrameworkPropertyMetadata(1.85, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SfIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double Weight { get => (double)GetValue(WeightProperty); set => SetValue(WeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public SfIcon() { SnapsToDevicePixels = false; }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size available) =>
        string.IsNullOrEmpty(Kind) ? new System.Windows.Size(0, 0) : new System.Windows.Size(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var (stroke, fill) = Icons.Get(Kind);
        if (stroke == null && fill == null) return;
        double s = Size / 24.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - Size) / 2, (ActualHeight - Size) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        if (fill != null) dc.DrawGeometry(Foreground, null, fill);
        if (stroke != null)
        {
            var pen = new Pen(Foreground, Weight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, stroke);
        }
        dc.Pop();
        dc.Pop();
    }
}
