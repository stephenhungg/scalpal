// IMGUI signatures for compilation, not a screen rendering or user interaction test.
namespace UnityEngine
{
    public struct Vector2 { }
    public struct Rect { public Rect(float x, float y, float width, float height) { } }
    public static class Screen { public static int width = 1280, height = 720; }
    public class GUIStyle { }
    public class GUISkin { public GUIStyle box = new GUIStyle(); }
    public static class GUI { public static GUISkin skin = new GUISkin(); }
    public class GUILayoutOption { }
    public static class GUILayout
    {
        public static void BeginArea(Rect rect, GUIStyle style) { }
        public static void EndArea() { }
        public static Vector2 BeginScrollView(Vector2 scroll) => scroll;
        public static void EndScrollView() { }
        public static void BeginHorizontal() { }
        public static void EndHorizontal() { }
        public static void Label(string text, params GUILayoutOption[] options) { }
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static string TextField(string text) => text;
        public static void Space(float pixels) { }
        public static GUILayoutOption Width(float width) => new GUILayoutOption();
    }
}
