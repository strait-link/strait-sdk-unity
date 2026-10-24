// Just enough UnityEngine surface for the lint build. Signatures follow the Unity scripting API.
#nullable disable
using System;

namespace UnityEngine
{
    public class Object { public static void DontDestroyOnLoad(Object target) { } }
    public class Component : Object { public GameObject gameObject => null; }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public sealed class GameObject : Object { }
    public class TextAsset : Object { public string text => ""; }
    public sealed class SerializeField : Attribute { }
    public enum RuntimePlatform { OSXEditor, Android, IPhonePlayer }
    public static class Application
    {
        public static RuntimePlatform platform => RuntimePlatform.OSXEditor;
        public static string absoluteURL => "";
        public static event Action<string> deepLinkActivated { add { } remove { } }
    }
    public static class Resources { public static T Load<T>(string path) where T : Object => null; }
    public static class JsonUtility { public static T FromJson<T>(string json) => default; }
    public static class PlayerPrefs
    {
        public static bool HasKey(string key) => false;
        public static string GetString(string key) => "";
        public static void SetString(string key, string value) { }
        public static void Save() { }
    }
    public static class Debug { public static void Log(object message) { } }
    public static class GUILayout
    {
        public static bool Button(string text, params object[] options) => false;
        public static void Label(string text, params object[] options) { }
    }
}
