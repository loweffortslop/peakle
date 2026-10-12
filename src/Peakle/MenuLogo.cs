using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Peakle;

/// <summary>
/// Puts the Peakle logo where PEAK's logo is on the title screen. PEAK's code never references its logo (it lives in
/// the menu's prefab), so it is found by name: UI images and sprites whose object, sprite or texture name contains
/// "logo". Every candidate is logged, so a miss can be fixed from the BepInEx log.
/// </summary>
internal class MenuLogo : MonoBehaviour
{
    private static Texture2D logo;
    private static Sprite logoSprite;

    public static void Init(GameObject _)
    {
        // Its own object: BepInEx's manager object can be destroyed by the game on scene loads.
        GameObject host = new("Peakle Menu Logo");
        DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<MenuLogo>();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (Plugin.ReplaceMenuLogo.Value && scene.name.ToLowerInvariant().Contains("title"))
        {
            StartCoroutine(ReplaceSoon(scene));
        }
    }

    // The menu builds itself over the first frames; look a few times.
    private IEnumerator ReplaceSoon(Scene scene)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            yield return new WaitForSecondsRealtime(attempt == 0 ? 0.1f : 1f);
            if (Replace(scene) > 0)
            {
                yield break;
            }
        }
        Plugin.Log.LogWarning("Menu logo: no PEAK logo found to replace on the title screen");
    }

    private static int Replace(Scene scene)
    {
        if (!LoadLogo())
        {
            return 0;
        }
        int replaced = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                // Only PEAK's own title logo (and its shadow and black intro version); keep their colours.
                string path = Path(image.transform);
                if (path.EndsWith("MainPage/Logo/Logo") || path.EndsWith("MainPage/Logo/Shadow") || path.EndsWith("MainPage/LogoBlack"))
                {
                    Plugin.Log.LogInfo($"Menu logo: replacing Image '{path}'");
                    image.sprite = logoSprite;
                    image.overrideSprite = null;
                    image.preserveAspect = true;
                    replaced++;
                }
            }
            foreach (RawImage raw in root.GetComponentsInChildren<RawImage>(true))
            {
                if (IsLogo(raw.gameObject.name, null, raw.texture))
                {
                    Plugin.Log.LogInfo($"Menu logo: found RawImage '{Path(raw.transform)}' - not replaced");
                }
            }
            foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (IsLogo(sr.gameObject.name, sr.sprite != null ? sr.sprite.name : null, sr.sprite != null ? sr.sprite.texture : null))
                {
                    Plugin.Log.LogInfo($"Menu logo: found SpriteRenderer '{Path(sr.transform)}' - not replaced");
                }
            }
            // Logos drawn by a mesh and material aren't swapped blind (they may rely on a special shader), just reported.
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SpriteRenderer)
                {
                    continue;
                }
                Material m = r.sharedMaterial;
                Texture t = m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
                if (IsLogo(r.gameObject.name, m != null ? m.name : null, t))
                {
                    Plugin.Log.LogInfo($"Menu logo: found a {r.GetType().Name} '{Path(r.transform)}' (material '{(m != null ? m.name : "none")}') - not replaced");
                }
            }
        }
        return replaced;
    }

    // Keep the logo box's height and give it the Peakle logo's (wider) shape.
    private static void FitAspect(RectTransform rt)
    {
        float aspect = (float)logo.width / logo.height;
        Vector2 size = rt.rect.size;
        if (size.y > 1f)
        {
            rt.sizeDelta += new Vector2(size.y * aspect - size.x, 0f);
        }
    }

    private static bool IsLogo(string objectName, string spriteName, Texture texture)
    {
        return Has(objectName) || Has(spriteName) || (texture != null && texture != logo && Has(texture.name));
    }

    private static bool Has(string name) => !string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains("logo") && !name.Contains("Peakle");

    private static string Path(Transform t)
    {
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent)
        {
            path = p.name + "/" + path;
        }
        return path;
    }

    private static bool LoadLogo()
    {
        if (logo != null)
        {
            return true;
        }
        using Stream stream = typeof(MenuLogo).Assembly.GetManifestResourceStream("Peakle.Resources.logo.png");
        if (stream == null)
        {
            Plugin.Log.LogWarning("Menu logo: logo.png resource missing");
            return false;
        }
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        logo = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Peakle Logo" };
        if (!logo.LoadImage(bytes.ToArray()))
        {
            logo = null;
            return false;
        }
        logoSprite = Sprite.Create(logo, new Rect(0, 0, logo.width, logo.height), new Vector2(0.5f, 0.5f), 100f);
        logoSprite.name = "Peakle Logo";
        return true;
    }
}
