using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Imagine.WebAR.Demo.Editor
{
    public class DemoMenu
    {
		[MenuItem("Assets/Imagine WebAR/Add Demo Imagetargets", false, 1301)]
		public static void AddDemoImageTargets()
		{
			var settings = ImageTrackerGlobalSettings.Instance;
			foreach(var info in GetDemoTargets())
            {
				var index = settings.imageTargetInfos.FindIndex(i => i.id == info.id);
				if(index < 0)
                {
					settings.imageTargetInfos.Add(info);
                }
            }
			EditorUtility.SetDirty(ImageTrackerGlobalSettings.Instance);
			Selection.activeObject = ImageTrackerGlobalSettings.Instance;
		}

		[MenuItem("Assets/Imagine WebAR/Remove Demo Imagetargets", false, 1302)]
		public static void RemoveDemoImageTargets()
		{
			var settings = ImageTrackerGlobalSettings.Instance;
			foreach (var info in GetDemoTargets())
			{
				var index = settings.imageTargetInfos.FindIndex(i => i.id == info.id);
				if (index >= 0)
				{
					settings.imageTargetInfos.RemoveAt(index);
				}
			}
			EditorUtility.SetDirty(ImageTrackerGlobalSettings.Instance);
			Selection.activeObject = ImageTrackerGlobalSettings.Instance;
		}

		static List<ImageTargetInfo> GetDemoTargets()
        {
			var infos = new List<ImageTargetInfo>();
			string[] files = Directory.GetFiles(Application.dataPath + "/Imagine/ImageTracker/Demos/Imagetargets", "*.prefab", SearchOption.TopDirectoryOnly);
			foreach(var file in files) {
				var id = Path.GetFileNameWithoutExtension(file);
				var path = file.Replace(Application.dataPath, "Assets");
				var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
				var tex = (Texture2D)prefab.GetComponent<Renderer>().sharedMaterial.mainTexture;
				infos.Add(new ImageTargetInfo() {
					id = id,
					texture = tex
				});
			}
			return infos;
		}
	}
}

