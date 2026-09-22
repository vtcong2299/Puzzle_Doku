using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BusFever
{
    /// <summary>
    /// Cuộn texture vô hạn NGAY TẠI CHỖ: ảnh chạy bên trong khung, GameObject không di chuyển.
    /// Tự nhận diện loại đối tượng và dùng cơ chế phù hợp cho từng loại:
    ///
    ///   • RawImage (UI)                      -> đổi uvRect. Chạy ngay với shader UI mặc định.
    ///   • TextMeshPro / TextMeshProUGUI      -> đổi _FaceTex_ST trên material instance.
    ///   • SpriteRenderer / MeshRenderer      -> đổi _MainTex_ST qua MaterialPropertyBlock.
    ///
    /// KHÔNG hỗ trợ Image (uGUI): Image vẽ qua CanvasRenderer nên MaterialPropertyBlock vô tác
    /// dụng, còn sửa offset trên material sẽ ảnh hưởng mọi Image dùng chung material đó.
    /// Hãy đổi sang RawImage. Script sẽ báo lỗi rõ nếu gắn nhầm.
    ///
    /// RIÊNG VỚI TMP — đọc kỹ:
    ///   Text TMP vẽ glyph từ font atlas, nên cuộn _MainTex sẽ cuộn chính cái atlas đó và chữ
    ///   sẽ vỡ thành ký tự loạn. Thứ cuộn được là _FaceTex (Face > Texture) — lớp texture phủ
    ///   lên mặt chữ, cho hiệu ứng gradient/ánh sáng chạy ngang chữ.
    ///   _FaceTex CHỈ có ở shader "TextMeshPro/Distance Field". Các bản "Mobile" đã lược bỏ nó.
    ///
    /// SETUP BẮT BUỘC:
    ///   1. Texture để Wrap Mode = Repeat (nếu không, ảnh bị kéo giãn ở mép thay vì lặp).
    ///   2. RawImage: kéo thẳng Texture vào ô Texture, không dùng Sprite.
    ///   3. TMP: material dùng shader Distance Field (bản đầy đủ) + gán texture vào Face > Texture.
    ///   4. Renderer: shader phải áp TRANSFORM_TEX/_MainTex_ST. "Sprites/Default" và Sprite
    ///      shader mặc định của URP BỎ QUA _MainTex_ST nên ảnh sẽ đứng yên.
    ///   5. Sprite không được nằm trong Sprite Atlas.
    ///
    /// Script tự kiểm tra các điều kiện trên lúc chạy và log cảnh báo cụ thể nếu thiếu.
    /// </summary>
    [DisallowMultipleComponent]
    public class Scroll_Texture : MonoBehaviour
    {
        private enum TargetKind
        {
            None,
            RawImage,
            Text,
            Renderer
        }

        [Header("Tốc độ")]
        [Tooltip("Số lần texture trôi hết một vòng trong 1 giây. Dương/âm để đổi chiều.")]
        [SerializeField] private Vector2 speed = new Vector2(0.5f, 0f);

        [Header("Lặp")]
        [Tooltip("Số lần texture lặp lại trong khung. (1,1) = đúng một lần.")]
        [SerializeField] private Vector2 tiling = Vector2.one;

        [Header("Tuỳ chọn")]
        [Tooltip("Bật nếu muốn tiếp tục chạy khi game pause bằng Time.timeScale = 0.")]
        [SerializeField] private bool useUnscaledTime = true;

        [Header("Tên property (chỉ đổi khi dùng shader tự viết)")]
        [Tooltip("Dùng cho SpriteRenderer/MeshRenderer. Unlit/Sprite dùng _MainTex, URP Lit dùng _BaseMap.")]
        [SerializeField] private string rendererTextureProperty = "_MainTex";

        [Tooltip("Dùng cho TextMeshPro. Mặc định _FaceTex (Face > Texture). " +
                 "Đổi sang _OutlineTex nếu muốn cuộn lớp viền thay vì mặt chữ.")]
        [SerializeField] private string textTextureProperty = "_FaceTex";

        private TargetKind targetKind = TargetKind.None;

        private RawImage rawImage;
        private TMP_Text text;
        private new Renderer renderer;

        // TMP phải dùng material instance: TextMeshProUGUI vẽ qua CanvasRenderer nên
        // MaterialPropertyBlock không có tác dụng. fontMaterial do chính TMP quản lý
        // vòng đời, không được tự Destroy.
        private Material textMaterial;

        private MaterialPropertyBlock propertyBlock;
        private int scaleOffsetPropertyId;

        private Vector2 offset;
        private bool isReady;

        public bool IsScrolling { get; private set; } = true;

        private void Awake()
        {
            Initialize();
        }

        private void OnEnable()
        {
            if (isReady) Apply();
        }

        private void Update()
        {
            if (!isReady || !IsScrolling) return;

            float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

            // Cộng dồn rồi Repeat về [0,1) thay vì dùng Time.time * speed.
            // Time.time tăng vô hạn nên sau vài chục phút chơi float mất độ chính xác
            // và ảnh bắt đầu nhảy từng bước thấy rõ.
            offset.x = Mathf.Repeat(offset.x + speed.x * dt, 1f);
            offset.y = Mathf.Repeat(offset.y + speed.y * dt, 1f);

            Apply();
        }

        #region INIT

        private void Initialize()
        {
            if (isReady) return;

            // Thứ tự kiểm tra quan trọng: TextMeshPro (bản 3D) cũng có MeshRenderer,
            // nên phải bắt TMP_Text TRƯỚC Renderer, nếu không sẽ rơi nhầm nhánh
            // và đi cuộn font atlas.
            if (TryGetComponent(out rawImage))
            {
                targetKind = TargetKind.RawImage;
            }
            else if (TryGetComponent(out text))
            {
                targetKind = TargetKind.Text;
                textMaterial = text.fontMaterial;
                scaleOffsetPropertyId = ResolvePropertyId(ref textTextureProperty, "_FaceTex");
            }
            else if (TryGetComponent(out renderer))
            {
                targetKind = TargetKind.Renderer;
                scaleOffsetPropertyId = ResolvePropertyId(ref rendererTextureProperty, "_MainTex");
                propertyBlock = new MaterialPropertyBlock();
            }
            else
            {
                LogMissingTarget();
                enabled = false;
                return;
            }

            isReady = true;

            Validate();
            Apply();
        }

        /// <summary>Tiling/offset của property "_Foo" luôn nằm ở vector "_Foo_ST".</summary>
        private static int ResolvePropertyId(ref string propertyName, string fallback)
        {
            if (string.IsNullOrEmpty(propertyName))
                propertyName = fallback;

            return Shader.PropertyToID(propertyName + "_ST");
        }

        #endregion

        private void Apply()
        {
            switch (targetKind)
            {
                case TargetKind.RawImage:
                    // uvRect: (x, y) là offset, (width, height) là số lần lặp.
                    rawImage.uvRect = new Rect(offset.x, offset.y, tiling.x, tiling.y);
                    break;

                case TargetKind.Text:
                    if (textMaterial == null) return;
                    textMaterial.SetVector(
                        scaleOffsetPropertyId,
                        new Vector4(tiling.x, tiling.y, offset.x, offset.y));
                    break;

                case TargetKind.Renderer:
                    renderer.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetVector(
                        scaleOffsetPropertyId,
                        new Vector4(tiling.x, tiling.y, offset.x, offset.y));
                    renderer.SetPropertyBlock(propertyBlock);
                    break;
            }
        }

        #region PUBLIC API

        public void SetSpeed(Vector2 value) => speed = value;

        public void SetTiling(Vector2 value)
        {
            tiling = value;
            if (isReady) Apply();
        }

        public void Pause() => IsScrolling = false;

        public void Resume() => IsScrolling = true;

        /// <summary>Đưa ảnh về vị trí ban đầu.</summary>
        public void ResetOffset()
        {
            offset = Vector2.zero;
            if (isReady) Apply();
        }

        /// <summary>
        /// Gọi sau khi đổi font hoặc material của TMP lúc runtime:
        /// fontMaterial cũ đã bị thay nên tham chiếu đang cache không còn đúng.
        /// </summary>
        public void RefreshTarget()
        {
            if (targetKind != TargetKind.Text || text == null) return;

            textMaterial = text.fontMaterial;
            Apply();
        }

        #endregion

        #region VALIDATE

        /// <summary>
        /// Các lỗi setup dưới đây đều khiến script "chạy" mà màn hình không đổi gì,
        /// rất khó đoán nếu không được báo rõ.
        /// </summary>
        private void Validate()
        {
            switch (targetKind)
            {
                case TargetKind.RawImage:
                    ValidateRawImage();
                    break;

                case TargetKind.Text:
                    ValidateText();
                    break;

                case TargetKind.Renderer:
                    ValidateRenderer();
                    break;
            }
        }

        private void LogMissingTarget()
        {
            // Trường hợp hay gặp nhất: gắn nhầm vào Image thay vì RawImage.
            if (TryGetComponent(out Image _))
            {
                Debug.LogError(
                    $"{nameof(Scroll_Texture)}: component Image (uGUI) không cuộn được. Image vẽ qua " +
                    "CanvasRenderer nên MaterialPropertyBlock vô tác dụng, còn sửa offset trên " +
                    "material sẽ ảnh hưởng mọi Image dùng chung material. Hãy thay Image bằng " +
                    "RawImage rồi kéo Texture vào.", this);
                return;
            }

            Debug.LogError(
                $"{nameof(Scroll_Texture)}: cần RawImage, TextMeshPro/TextMeshProUGUI, " +
                "hoặc Renderer (SpriteRenderer/MeshRenderer) trên cùng GameObject.", this);
        }

        private void ValidateRawImage()
        {
            if (rawImage.texture == null)
            {
                Debug.LogWarning($"{nameof(Scroll_Texture)}: RawImage chưa có Texture.", this);
                return;
            }

            WarnIfNotRepeating(rawImage.texture);
        }

        private void ValidateText()
        {
            if (textMaterial == null)
            {
                Debug.LogWarning($"{nameof(Scroll_Texture)}: TMP chưa có font material.", this);
                return;
            }

            if (!textMaterial.HasProperty(textTextureProperty))
            {
                Debug.LogWarning(
                    $"{nameof(Scroll_Texture)}: shader \"{textMaterial.shader.name}\" không có " +
                    $"property \"{textTextureProperty}\". Các shader TMP bản \"Mobile\" đã lược bỏ " +
                    "Face Texture — hãy đổi material sang shader \"TextMeshPro/Distance Field\" " +
                    "(bản đầy đủ).", this);
                return;
            }

            Texture faceTexture = textMaterial.GetTexture(textTextureProperty);

            if (faceTexture == null)
            {
                Debug.LogWarning(
                    $"{nameof(Scroll_Texture)}: chưa gán texture vào \"{textTextureProperty}\" " +
                    "(mục Face > Texture trong material TMP). Chưa có texture thì không có gì " +
                    "để cuộn cả.", this);
                return;
            }

            WarnIfNotRepeating(faceTexture);
        }

        private void ValidateRenderer()
        {
            Material material = renderer.sharedMaterial;

            if (material == null)
            {
                Debug.LogWarning($"{nameof(Scroll_Texture)}: Renderer chưa có material.", this);
                return;
            }

            if (!material.HasProperty(rendererTextureProperty))
            {
                Debug.LogWarning(
                    $"{nameof(Scroll_Texture)}: shader \"{material.shader.name}\" không có property " +
                    $"\"{rendererTextureProperty}\". Kiểm tra lại tên property.", this);
                return;
            }

            WarnIfShaderIgnoresOffset(material);

            Texture texture = ResolveRendererTexture(material);
            if (texture != null) WarnIfNotRepeating(texture);
        }

        private void WarnIfShaderIgnoresOffset(Material material)
        {
            string shaderName = material.shader.name;

            // Các shader sprite dựng sẵn truyền thẳng UV mesh, không gọi TRANSFORM_TEX,
            // nên _MainTex_ST hoàn toàn không có tác dụng.
            bool ignoresScaleOffset =
                shaderName == "Sprites/Default" ||
                shaderName == "Sprites/Diffuse" ||
                shaderName.Contains("Sprite-Unlit-Default") ||
                shaderName.Contains("Sprite-Lit-Default");

            if (!ignoresScaleOffset) return;

            Debug.LogWarning(
                $"{nameof(Scroll_Texture)}: material đang dùng shader \"{shaderName}\", shader này " +
                "bỏ qua tiling/offset nên ảnh sẽ ĐỨNG YÊN. Đổi sang material dùng Unlit/Texture, " +
                "Unlit/Transparent hoặc shader tự viết có TRANSFORM_TEX.", this);
        }

        private void WarnIfNotRepeating(Texture texture)
        {
            if (texture.wrapMode == TextureWrapMode.Repeat) return;

            Debug.LogWarning(
                $"{nameof(Scroll_Texture)}: texture \"{texture.name}\" đang để Wrap Mode = " +
                $"{texture.wrapMode}. Đổi sang Repeat, nếu không ảnh sẽ bị kéo giãn ở mép " +
                "thay vì lặp vô hạn.", this);
        }

        /// <summary>
        /// Với SpriteRenderer, texture nằm ở sprite chứ không phải ở material
        /// (SpriteRenderer tự gán _MainTex lúc vẽ), nên phải lấy theo đường khác.
        /// </summary>
        private Texture ResolveRendererTexture(Material material)
        {
            if (renderer is SpriteRenderer spriteRenderer)
            {
                if (spriteRenderer.sprite == null) return null;

                if (spriteRenderer.sprite.packed)
                {
                    Debug.LogWarning(
                        $"{nameof(Scroll_Texture)}: sprite \"{spriteRenderer.sprite.name}\" nằm trong " +
                        "Sprite Atlas. UV của nó chỉ chiếm một phần atlas nên khi cuộn sẽ lòi sang " +
                        "ảnh bên cạnh. Hãy để sprite này ngoài atlas.", this);
                }

                return spriteRenderer.sprite.texture;
            }

            return material.GetTexture(rendererTextureProperty);
        }

        #endregion

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Cho phép chỉnh tiling/tốc độ và thấy kết quả ngay trong Inspector lúc đang Play.
            if (!Application.isPlaying || !isReady) return;
            Apply();
        }
#endif
    }
}
