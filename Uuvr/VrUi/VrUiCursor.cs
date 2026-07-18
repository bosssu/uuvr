using System.IO;
using UnityEngine;

namespace Uuvr.VrUi;

/// <summary>
/// Software mouse cursor for VR UI.
/// <list type="bullet">
/// <item>Mirror / game-view: ForceSoftware so the cursor is baked into the framebuffer capture.</item>
/// <item>Non-HDRP CanvasRedirect: UI is a dedicated capture-camera RT — OS/software cursor never
/// lands there, so we composite the cursor onto that RT after the capture camera renders.
/// HDRP World Space uses <see cref="VrUiWorldInput"/>'s RawImage cursor (untouched here).</item>
/// </list>
/// </summary>
public class VrUiCursor : UuvrBehaviour
{
#if CPP
    public VrUiCursor(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private Texture2D? _texture;
    private RenderTexture? _uiTexture;
    private Material? _drawMaterial;
    private readonly Vector2 _offset = new(22f, 2f);
    private bool _loggedComposite;
    private bool _loggedMissingAsset;

    /// <summary>UI capture / plate RenderTexture (non-HDRP). Null on HDRP world-space path.</summary>
    public void SetUiTexture(RenderTexture? texture)
    {
        _uiTexture = texture;
    }

    private void Start()
    {
        LoadTexture();
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        // Built-in pipeline: fires after each Camera finishes (including targetTexture captures).
        Camera.onPostRender += OnCameraPostRender;
    }

    protected override void OnDisable()
    {
        Camera.onPostRender -= OnCameraPostRender;
        base.OnDisable();
    }

    private void OnDestroy()
    {
        if (_drawMaterial != null)
        {
            Destroy(_drawMaterial);
            _drawMaterial = null;
        }
    }

    private void Update()
    {
        if (_texture == null) return;

        // Games often reset the cursor; re-assert software mode for Mirror / desktop capture.
        // For CanvasRedirect RT path the composite below is what actually shows in the HMD.
        Cursor.SetCursor(_texture, _offset, CursorMode.ForceSoftware);

        if (ShouldCompositeOntoCaptureRt())
        {
            // Hide hardware cursor; HMD uses the composited sprite. Desktop may still show SW cursor.
            Cursor.visible = true;
        }
    }

    private void OnCameraPostRender(Camera cam)
    {
        if (!ShouldCompositeOntoCaptureRt()) return;
        if (_texture == null || _uiTexture == null || !_uiTexture.IsCreated()) return;
        if (cam == null || cam.targetTexture != _uiTexture) return;

        DrawCursorOntoRt(_uiTexture);

        if (!_loggedComposite)
        {
            _loggedComposite = true;
            Debug.Log(
                $"[UUVR] VrUiCursor: compositing onto capture RT " +
                $"{_uiTexture.width}x{_uiTexture.height} (non-HDRP CanvasRedirect)");
        }
    }

    /// <summary>
    /// Only the non-HDRP ScreenSpaceCamera capture plate needs RT compositing.
    /// HDRP world-space: VrUiWorldInput. Mirror: ForceSoftware + game-view capture.
    /// </summary>
    private static bool ShouldCompositeOntoCaptureRt()
    {
        if (UiCameraSetup.IsHdrp()) return false;
        try
        {
            return ModConfiguration.Instance.PreferredUiPatchMode.Value
                   == ModConfiguration.UiPatchMode.CanvasRedirect;
        }
        catch
        {
            return false;
        }
    }

    private void DrawCursorOntoRt(RenderTexture rt)
    {
        var mouse = Input.mousePosition;
        // Optional geometry flip on the VR quad does not flip RT contents; keep screen mapping.
        var hotX = _offset.x;
        var hotY = _offset.y;
        var w = _texture!.width;
        var h = _texture.height;

        // Input.mousePosition: origin bottom-left. Draw with top-left pixel matrix to match
        // Cursor.SetCursor hotspot (top-left of the texture).
        var x = mouse.x - hotX;
        var yFromTop = (rt.height - mouse.y) - hotY;

        EnsureDrawMaterial();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0f, rt.width, rt.height, 0f);
        var rect = new Rect(x, yFromTop, w, h);
        if (_drawMaterial != null)
        {
            Graphics.DrawTexture(rect, _texture, _drawMaterial);
        }
        else
        {
            Graphics.DrawTexture(rect, _texture);
        }
        GL.PopMatrix();
        RenderTexture.active = prev;
    }

    private void EnsureDrawMaterial()
    {
        if (_drawMaterial != null) return;

        // Prefer shaders that respect texture alpha on built-in (legacy mono games).
        var shader = Shader.Find("Hidden/Internal-GUITexture")
                     ?? Shader.Find("UI/Default")
                     ?? Shader.Find("Unlit/Transparent")
                     ?? Shader.Find("Sprites/Default");
        if (shader == null) return;

        _drawMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave,
            mainTexture = _texture,
            color = Color.white
        };
        if (_drawMaterial.HasProperty("_Color"))
        {
            _drawMaterial.SetColor("_Color", Color.white);
        }
    }

    private void LoadTexture()
    {
        if (_texture != null) return;
        try
        {
            var path = Path.Combine(Path.Combine(UuvrPlugin.ModFolderPath, "Assets"), "cursor.bmp");
            if (!File.Exists(path))
            {
                // Fallback: legacy path segment used by older builds
                path = Path.Combine(UuvrPlugin.ModFolderPath, @"Assets\cursor.bmp");
            }

            if (!File.Exists(path))
            {
                if (!_loggedMissingAsset)
                {
                    _loggedMissingAsset = true;
                    Debug.LogWarning($"[UUVR] VrUiCursor: missing cursor.bmp under {UuvrPlugin.ModFolderPath}");
                }
                return;
            }

            var bytes = File.ReadAllBytes(path);
            var width = bytes[18] + (bytes[19] << 8);
            var height = bytes[22] + (bytes[23] << 8);

            var colors = new Color32[width * height];
            _texture = new Texture2D(width, height, TextureFormat.BGRA32, false);
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color32(
                    bytes[i * 4 + 54],
                    bytes[i * 4 + 55],
                    bytes[i * 4 + 56],
                    bytes[i * 4 + 57]);
            }

            _texture.SetPixels32(colors);
            _texture.Apply(false, false);
            Debug.Log($"[UUVR] VrUiCursor texture loaded {width}x{height} from {path}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[UUVR] VrUiCursor load failed: {e.Message}");
        }
    }
}
