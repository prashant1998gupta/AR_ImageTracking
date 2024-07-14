using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace Imagine.WebAR.Editor
{
	public class ImageWebARMenu
	{
		[MenuItem("Assets/Imagine WebAR/Create/Image Target", false, 1010)]
		public static void CreateImageTarget()
		{
			if(Selection.activeObject == null || !(Selection.activeObject is Texture2D)){
				EditorUtility.DisplayDialog("No Image Selected", "Please select an image in your project first", "OK");
				return;
			}

			var texture = (Texture2D)Selection.activeObject;

			var texturePath = AssetDatabase.GetAssetPath(texture);
			Debug.Log(texture.name + ": " + texture.width + "x" + texture.height);

			var savePath = EditorUtility.SaveFilePanel("Save Imagetarget Prefab", texturePath, texture.name, "prefab");
			var id = Path.GetFileNameWithoutExtension(savePath);
			Debug.Log(savePath);

			var go = CreateMesh(texture);
			go.name = id;
			Selection.activeGameObject = go;


			var mat = new Material(Shader.Find("Unlit/Texture"));
			mat.mainTexture = texture;

			var matDir = Application.dataPath + "/Imagine/ImageTracker/Imagetargets/Materials/";
			if (!Directory.Exists(matDir))
            {
				Directory.CreateDirectory(matDir);
            }
			AssetDatabase.CreateAsset(mat, "Assets/Imagine/ImageTracker/Imagetargets/Materials/" + id + " Material.mat");

			var mesh = go.GetComponent<MeshFilter>().sharedMesh;
			var meshDir = Application.dataPath + "/Imagine/ImageTracker/Imagetargets/Meshes/";
			if (!Directory.Exists(meshDir))
			{
				Directory.CreateDirectory(meshDir);
			}
			AssetDatabase.CreateAsset(mesh, "Assets/Imagine/ImageTracker/Imagetargets/Meshes/" + id + " Mesh.asset");


			var renderer = go.AddComponent<MeshRenderer>();
			renderer.material = mat;

			PrefabUtility.SaveAsPrefabAsset(go, savePath);
			PrefabUtility.SaveAsPrefabAssetAndConnect(go, savePath, InteractionMode.AutomatedAction);

			ImageTrackerGlobalSettings.Instance.imageTargetInfos.Add(new ImageTargetInfo()
			{
				id = id,
				texture = texture
			});
			EditorUtility.SetDirty(ImageTrackerGlobalSettings.Instance);


			var tracker = GameObject.FindObjectOfType<ImageTracker>();
			if (tracker != null)
			{

				var so = new SerializedObject(tracker);
				var sp = so.FindProperty("imageTargets");

				go.transform.position = new Vector3((sp.arraySize), 0, 0);
				go.transform.parent = tracker.transform;

				sp.arraySize++;
				so.ApplyModifiedProperties();

				var obj = sp.GetArrayElementAtIndex(sp.arraySize - 1);
				obj.FindPropertyRelative("id").stringValue = id;
				obj.FindPropertyRelative("transform").objectReferenceValue = go.transform;

				so.ApplyModifiedProperties();
			}
		}

		// [MenuItem("Assets/Imagine WebAR/Create/Image Target", true)]
		// static bool ValidateCreateImageTarget()
		// {
		// 	var asset = Selection.activeObject;
		// 	return (asset != null && asset is Texture2D);
		// }

		static GameObject CreateMesh(Texture2D texture)
		{
			var go = new GameObject();
			MeshFilter filter = go.AddComponent<MeshFilter>();
			Mesh mesh = new Mesh();
			filter.mesh = mesh;
			mesh.Clear();

			int resX = 2;
			int resY = 2;

			int actualWidth = 0;
			int actualHeight = 0;
			GetActualTextureSize(texture, ref actualWidth, ref actualHeight);

			if (actualWidth <= 0 || actualWidth <= 0)
			{
				EditorUtility.DisplayDialog("Failed to create Imagetarget", "Texture width and/or height cannot be zero", "Ok");
				throw new System.ArgumentException("Texture width and / or height cannot be zero");
			}


			float maxSide = Mathf.Max(actualWidth, actualHeight);

			float width = (float)actualWidth / maxSide;
			float height = (float)actualHeight / maxSide;


			Debug.Log("Mesh size = " + height + "x" + width);


			Vector3[] vertices = new Vector3[resX * resY];
			for (int y = 0; y < resY; y++)
			{
				float yPos = ((float)y / (resY - 1) - .5f) * height;
				for (int x = 0; x < resX; x++)
				{
					float xPos = ((float)x / (resX - 1) - .5f) * width;
					vertices[x + y * resX] = new Vector3(xPos, yPos, 0f);
				}
			}

			Vector3[] normals = new Vector3[vertices.Length];
			for (int n = 0; n < normals.Length; n++)
				normals[n] = Vector3.up;

			Vector2[] uvs = new Vector2[vertices.Length];
			for (int v = 0; v < resY; v++)
			{
				for (int u = 0; u < resX; u++)
				{
					uvs[u + v * resX] = new Vector2((float)u / (resX - 1), (float)v / (resY - 1));
				}
			}

			int nbFaces = (resX - 1) * (resY - 1);
			int[] triangles = new int[nbFaces * 6];
			int t = 0;
			for (int face = 0; face < nbFaces; face++)
			{
				int i = face % (resX - 1) + (face / (resY - 1) * resX);

				triangles[t++] = i + resX;
				triangles[t++] = i + 1;
				triangles[t++] = i;

				triangles[t++] = i + resX;
				triangles[t++] = i + resX + 1;
				triangles[t++] = i + 1;
			}

			mesh.vertices = vertices;
			mesh.normals = normals;
			mesh.uv = uvs;
			mesh.triangles = triangles;

			mesh.RecalculateBounds();

			return go;

		}

		private delegate void GetWidthAndHeight(TextureImporter importer, ref int width, ref int height);
		private static GetWidthAndHeight getWidthAndHeightDelegate;

		public static void GetActualTextureSize(Texture2D texture, ref int width, ref int height)
		{
			if (texture == null)
				throw new System.NullReferenceException();

			var path = AssetDatabase.GetAssetPath(texture);
			if (string.IsNullOrEmpty(path))
				throw new System.Exception("Texture2D is not an asset texture.");

			var importer = AssetImporter.GetAtPath(path) as TextureImporter;
			if (importer == null)
				throw new System.Exception("Failed to get Texture importer for " + path);


			if (getWidthAndHeightDelegate == null)
			{
				var method = typeof(TextureImporter).GetMethod("GetWidthAndHeight", BindingFlags.NonPublic | BindingFlags.Instance);
				getWidthAndHeightDelegate = System.Delegate.CreateDelegate(typeof(GetWidthAndHeight), null, method) as GetWidthAndHeight;
			}

			getWidthAndHeightDelegate(importer, ref width, ref height);
		}

		[MenuItem("Assets/Imagine WebAR/Create/Image Tracker", false, 1100)]
		public static void CreateImageTracker()
		{
			GameObject prefab = (GameObject)AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Imagine/ImageTracker/Prefabs/ImageTracker.prefab");
			GameObject gameObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
			PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
			Selection.activeGameObject = gameObject;
			gameObject.name = "ImageTracker";
		}

		[MenuItem("Assets/Imagine WebAR/Create/AR Camera", false, 1120)]
		public static void CreateImageTrackerCamera()
		{
			GameObject prefab = (GameObject)AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Imagine/ImageTracker/Prefabs/ARCamera.prefab");
			GameObject gameObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
			PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
			Selection.activeGameObject = gameObject;
			gameObject.name = "ARCamera";
		}

		[MenuItem("Assets/Imagine WebAR/Update Plugin to URP", false, 1200)]
		public static void SetURP()
		{
			if (EditorUtility.DisplayDialog(
				"Update Imagine WebAR Plugin to URP",
				"Please make sure that the Universal RP package is already installed before doing this step.",
				"Proceed",
				"Cancel"))
			{
				string[] files = Directory.GetFiles(Application.dataPath + "/Imagine/ImageTracker/Demos/Materials", "*.mat", SearchOption.TopDirectoryOnly);
				foreach (var file in files)
				{
					var path = file.Replace(Application.dataPath, "Assets");
					var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
					if (mat.shader.name == "Standard")
					{
						mat.shader = Shader.Find("Universal Render Pipeline/Lit");
					}
					else if (mat.shader.name == "Imagine/ARShadow")
                    {
						mat.shader = Shader.Find("Imagine/ARShadowURP");
					}
				}

				AddDefineSymbol("IMAGINE_URP");
				EditorUtility.DisplayDialog("Completed", "Imagine WebAR Plugin is now set to URP. \n\nSome URP features such as HDR and Post-Processing may be partially/fully unsupported.", "Ok");
			}
		}


		[MenuItem("Assets/Imagine WebAR/Roll-back Plugin to Built-In RP", false, 1201)]
		public static void SetBuiltInRP ()
		{
			if (EditorUtility.DisplayDialog(
				"Roll-back Imagine WebAR Plugin to Built-In RP",
				"Plese confirm.",
				"Proceed",
				"Cancel"))
			{
				string[] files = Directory.GetFiles(Application.dataPath + "/Imagine/ImageTracker/Demos/Materials", "*.mat", SearchOption.TopDirectoryOnly);
				foreach (var file in files)
				{
					var path = file.Replace(Application.dataPath, "Assets");
					var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
					if (mat.shader.name == "Universal Render Pipeline/Lit" || mat.shader.name == "Hidden/InternalErrorShader")
					{
						mat.shader = Shader.Find("Standard");
					}
					else if (mat.shader.name == "Imagine/ARShadowURP")
					{
						mat.shader = Shader.Find("Imagine/ARShadow");
					}
				}

				RemoveDefineSymbol("IMAGINE_URP");

				EditorUtility.DisplayDialog("Completed", "Imagine WebAR Plugin is now set to Built-In RP. Some edited materials may still require manual shader change","Ok");

			}
		}

		public static void AddDefineSymbol(string symbol)
		{
			string definesString = PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
			List<string> allDefines = definesString.Split(';').ToList();
			if (!allDefines.Contains(symbol))
				allDefines.Add(symbol);

			PlayerSettings.SetScriptingDefineSymbolsForGroup(
				 EditorUserBuildSettings.selectedBuildTargetGroup,
				 string.Join(";", allDefines.ToArray()));
			AssetDatabase.RefreshSettings();
		}

		public static void RemoveDefineSymbol(string symbol)
		{
			string definesString = PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
			List<string> allDefines = definesString.Split(';').ToList();
			allDefines.RemoveAll(s => s == symbol);
			PlayerSettings.SetScriptingDefineSymbolsForGroup(
				 EditorUserBuildSettings.selectedBuildTargetGroup,
				 string.Join(";", allDefines.ToArray()));
			AssetDatabase.RefreshSettings();

		}
	}
}

