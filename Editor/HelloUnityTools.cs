// This confirms your package loads and menus work.

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class HelloUnityTools
{
    [MenuItem("Tools/Unity Tools/Hello")]
    public static void Hello()
    {
        Debug.Log("Hello from com.shen-fengming.unitytools!");
    }
}
#endif