using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Cuộn texture vô hạn NGAY TẠI CHỖ: ảnh chạy bên trong khung, GameObject không di chuyển.
///
/// CÁCH DÙNG: chọn <see cref="TargetType"/> -> kéo Texture vào ô Texture -> set Speed. Hết.
/// Không cần tự tạo material, không cần gán texture vào material bằng tay.
///
/// Mỗi loại target dùng một cơ chế khác nhau, script tự lo phần này:
///   • RawImage      -> gán thẳng vào rawImage.texture, cuộn bằng uvRect.
///   • Image         -> tạo Sprite runtime từ Texture, cuộn _MainTex_ST trên material instance.
///   • SpriteRenderer-> tạo Sprite runtime từ Texture, cuộn _MainTex_ST qua MaterialPropertyBlock.
///   • MeshRenderer  -> gán _MainTex trên material instance, cuộn _MainTex_ST qua MaterialPropertyBlock.
///   • Text (TMP)    -> gán _FaceTex trên font material, cuộn _FaceTex_ST.
///
/// RIÊNG VỚI TMP — đọc kỹ:
///   Text TMP vẽ glyph từ font atlas, nên cuộn _MainTex sẽ cuộn chính cái atlas đó và chữ
///   sẽ vỡ thành ký tự loạn. Thứ cuộn được là _FaceTex (Face > Texture) — lớp texture phủ
///   lên mặt chữ, cho hiệu ứng gradient/ánh sáng chạy ngang chữ.
///   _FaceTex CHỈ có ở shader "TextMeshPro/Distance Field". Các bản "Mobile" đã lược bỏ nó.
///
/// LƯU Ý VỀ TEXTURE (bắt buộc, script không sửa hộ được vì đây là import setting):
///   • Wrap Mode = Repeat. Nếu để Clamp, ảnh bị kéo giãn ở mép thay vì lặp vô hạn.
///   • Với SpriteRenderer/Image: texture phải đọc được như Texture2D thường, KHÔNG dùng
///     sprite đã đóng gói trong Sprite Atlas (UV chỉ chiếm một phần atlas nên cuộn sẽ lòi
///     sang ảnh bên cạnh).
///
/// Script tự kiểm tra các điều kiện trên lúc chạy và log cảnh báo cụ thể nếu thiếu.
/// </summary>
[DisallowMultipleComponent]
public class Scroll_Texture : MonoBehaviour
{
    public enum TargetType
    {
        /// <summary>Tự dò component có sẵn trên GameObject.</summary>
        Auto,
        RawImage,
        Image,
        SpriteRenderer,
        MeshRenderer,

        /// <summary>TextMeshPro (3D) và TextMeshProUGUI đều dùng chung nhánh này.</summary>
        Text
    }

    #region INSPECTOR

    [Header("Đối tượng cuộn")]
    [Tooltip("Chọn loại component sẽ nhận texture. Auto = tự dò component có sẵn trên GameObject.")]
    [SerializeField]
    private TargetType targetType = TargetType.Auto;

    [Tooltip("Texture cần cuộn. Kéo thẳng vào đây, script tự gắn vào đúng chỗ của từng loại target.\n" +
             "Để trống nếu muốn giữ nguyên ảnh/sprite đang có sẵn và chỉ cuộn nó.")]
    [SerializeField]
    private Texture2D texture;

    [Tooltip("GameObject chứa component cần cuộn. Để trống = chính GameObject này.")]
    [SerializeField]
    private GameObject targetObject;

    [Tooltip("Cho phép tìm component trong cả các con. Dùng khi script nằm ở node cha " +
             "(ví dụ script ở object cha, TextMeshPro nằm ở con).")]
    [SerializeField]
    private bool searchInChildren;

    [Header("Tốc độ")]
    [Tooltip("Số lần texture trôi hết một vòng trong 1 giây. Dương/âm để đổi chiều.")]
    [SerializeField]
    private Vector2 speed = new Vector2(0.5f, 0f);

    [Header("Nâng cao")]
    [Tooltip("Số lần texture lặp lại trong khung. (1,1) = đúng một lần.")]
    [SerializeField]
    private Vector2 tiling = Vector2.one;

    [Tooltip("Bật nếu muốn tiếp tục chạy khi game pause bằng Time.timeScale = 0.")]
    [SerializeField]
    private bool useUnscaledTime = true;

    [Tooltip("Tự thay material khi shader hiện tại bỏ qua tiling/offset (Sprites/Default...).\n" +
             "Tắt nếu bạn đang dùng shader tự viết và không muốn bị đụng vào.")]
    [SerializeField]
    private bool autoFixMaterial = true;

    [Tooltip("Pixels Per Unit cho Sprite được tạo từ Texture (chỉ dùng cho SpriteRenderer/Image).\n" +
             "Nếu đã có sprite sẵn, script lấy theo sprite đó và bỏ qua ô này.")]
    [SerializeField]
    private float pixelsPerUnit = 100f;

    [Header("Tên property (chỉ đổi khi dùng shader tự viết)")]
    [Tooltip("Dùng cho SpriteRenderer/MeshRenderer. Unlit/Sprite dùng _MainTex, URP Lit dùng _BaseMap.")]
    [SerializeField]
    private string rendererTextureProperty = "_MainTex";

    [Tooltip("Dùng cho TextMeshPro. Mặc định _FaceTex (Face > Texture). " +
             "Đổi sang _OutlineTex nếu muốn cuộn lớp viền thay vì mặt chữ.")]
    [SerializeField]
    private string textTextureProperty = "_FaceTex";

    #endregion

    #region STATE

    private TargetType resolvedType = TargetType.Auto;

    private RawImage rawImage;
    private Image image;
    private TMP_Text text;
    private Renderer targetRenderer;

    // Graphic (Image) và TMP đều vẽ qua CanvasRenderer nên MaterialPropertyBlock vô tác dụng,
    // buộc phải ghi thẳng lên material instance.
    private Material graphicMaterial;

    // Những gì script tự tạo ra thì script phải tự dọn — xem OnDestroy.
    private Material ownedMaterial;
    private Sprite ownedSprite;

    private MaterialPropertyBlock propertyBlock;
    private int texturePropertyId;
    private int scaleOffsetPropertyId;

    private Vector2 offset;
    private bool isReady;
    private bool initAttempted;

    // Shader có thật sự khai báo property cần cuộn hay không. Bắt buộc phải kiểm tra TRƯỚC khi
    // ghi: Unity không chặn việc set property lạ, nó lưu luôn vào property sheet của material
    // thành một entry "mồ côi" mà shader không có. Inspector của TMP sau đó thấy HasProperty()
    // trả true nên gọi ShaderGUI.FindProperty() và ném ArgumentException mỗi lần vẽ.
    private bool hasTextureProperty;

    public bool IsScrolling { get; private set; } = true;

    #endregion

    #region UNITY

    // Cố tình dùng Start chứ không phải Awake: TMP_Text.fontMaterial chỉ hợp lệ sau khi TMP
    // tự Awake và load font asset xong. Script execution order giữa hai component là không
    // xác định, khởi tạo trong Awake sẽ có lúc vớ phải fontMaterial null.
    private void Start()
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

    private void OnDestroy()
    {
        if (ownedSprite != null) Destroy(ownedSprite);
        if (ownedMaterial != null) Destroy(ownedMaterial);
    }

    #endregion

    #region INIT

    /// <summary>
    /// Gọi được nhiều lần: lần đầu khởi tạo, các lần sau thoát ngay. Cần vậy vì script khác có
    /// thể gọi API public từ Awake/Start của nó, tức là trước Start của script này.
    /// </summary>
    private void Initialize()
    {
        if (isReady || initAttempted) return;

        initAttempted = true;

        if (!Bind())
        {
            enabled = false;
            return;
        }

        isReady = true;

        InjectTexture();
        RefreshPropertySupport();
        Validate();

        // Shader không có property để cuộn thì dừng hẳn, KHÔNG ghi gì lên material.
        // Validate() ở trên đã log lý do cụ thể rồi.
        if (!hasTextureProperty)
        {
            isReady = false;
            enabled = false;
            return;
        }

        ApplyTiling();
        Apply();
    }

    /// <summary>
    /// Kiểm tra shader hiện tại có khai báo property cần cuộn không.
    /// Phải hỏi SHADER chứ không hỏi Material: Material.HasProperty() còn trả true cho cả những
    /// entry mồ côi đã bị ghi nhầm vào property sheet trước đó, tức là đúng cái bẫy đang muốn tránh.
    /// </summary>
    private void RefreshPropertySupport()
    {
        switch (resolvedType)
        {
            case TargetType.RawImage:
                // uvRect không đụng tới material nên luôn cuộn được.
                hasTextureProperty = true;
                break;

            case TargetType.Image:
            case TargetType.Text:
                hasTextureProperty = ShaderHasProperty(graphicMaterial, CurrentTextureProperty);
                break;

            case TargetType.SpriteRenderer:
            case TargetType.MeshRenderer:
                Material material = ownedMaterial != null ? ownedMaterial : targetRenderer.sharedMaterial;
                hasTextureProperty = ShaderHasProperty(material, CurrentTextureProperty);
                break;
        }
    }

    private string CurrentTextureProperty =>
        resolvedType == TargetType.Text ? textTextureProperty : rendererTextureProperty;

    private static bool ShaderHasProperty(Material material, string propertyName)
    {
        if (material == null || material.shader == null) return false;

        return material.shader.FindPropertyIndex(propertyName) >= 0;
    }

    /// <summary>Tìm component tương ứng với <see cref="targetType"/> và cache lại.</summary>
    private bool Bind()
    {
        if (targetType != TargetType.Auto)
        {
            if (BindTo(targetType)) return true;

            Debug.LogError(
                $"{nameof(Scroll_Texture)}: đang chọn Target Type = {targetType} nhưng GameObject " +
                "không có component đó. Chọn lại Target Type hoặc thêm component tương ứng.", this);
            return false;
        }

        // Thứ tự kiểm tra quan trọng: TextMeshPro (bản 3D) cũng có MeshRenderer, nên phải bắt
        // TMP_Text TRƯỚC Renderer, nếu không sẽ rơi nhầm nhánh và đi cuộn font atlas.
        if (BindTo(TargetType.RawImage)) return true;
        if (BindTo(TargetType.Image)) return true;
        if (BindTo(TargetType.Text)) return true;
        if (BindTo(TargetType.SpriteRenderer)) return true;
        if (BindTo(TargetType.MeshRenderer)) return true;

        Debug.LogError(
            $"{nameof(Scroll_Texture)}: không tìm thấy component nào cuộn được trên GameObject. " +
            "Cần RawImage, Image, TextMeshPro/TextMeshProUGUI, SpriteRenderer hoặc MeshRenderer.", this);
        return false;
    }

    private bool BindTo(TargetType type)
    {
        switch (type)
        {
            case TargetType.RawImage:
                if (!TryFind(out rawImage)) return false;
                break;

            case TargetType.Image:
                if (!TryFind(out image)) return false;
                ResolvePropertyIds(ref rendererTextureProperty, "_MainTex");
                break;

            case TargetType.Text:
                if (!TryFind(out text)) return false;
                // fontMaterial tự clone ra instance riêng cho object này, lấy đúng MỘT lần
                // rồi cache — gọi lại mỗi frame là clone thêm material mới.
                graphicMaterial = text.fontMaterial;
                ResolvePropertyIds(ref textTextureProperty, "_FaceTex");
                break;

            case TargetType.SpriteRenderer:
                if (!TryFind(out SpriteRenderer spriteRenderer)) return false;
                targetRenderer = spriteRenderer;
                ResolvePropertyIds(ref rendererTextureProperty, "_MainTex");
                propertyBlock = new MaterialPropertyBlock();
                break;

            case TargetType.MeshRenderer:
                if (!TryFind(out MeshRenderer meshRenderer)) return false;
                targetRenderer = meshRenderer;
                ResolvePropertyIds(ref rendererTextureProperty, "_MainTex");
                propertyBlock = new MaterialPropertyBlock();
                break;

            default:
                return false;
        }

        resolvedType = type;
        return true;
    }

    private bool TryFind<T>(out T component) where T : Component
    {
        GameObject root = targetObject != null ? targetObject : gameObject;

        component = searchInChildren
            ? root.GetComponentInChildren<T>(true)
            : root.GetComponent<T>();

        return component != null;
    }

    /// <summary>Tiling/offset của property "_Foo" luôn nằm ở vector "_Foo_ST".</summary>
    private void ResolvePropertyIds(ref string propertyName, string fallback)
    {
        if (string.IsNullOrEmpty(propertyName))
            propertyName = fallback;

        texturePropertyId = Shader.PropertyToID(propertyName);
        scaleOffsetPropertyId = Shader.PropertyToID(propertyName + "_ST");
    }

    #endregion

    #region TEXTURE INJECTION

    /// <summary>
    /// Đẩy <see cref="texture"/> vào đúng chỗ của từng loại target. Texture để trống nghĩa là
    /// "giữ nguyên ảnh đang có", nhưng material thì vẫn phải chuẩn bị để cuộn được.
    /// </summary>
    private void InjectTexture()
    {
        switch (resolvedType)
        {
            case TargetType.RawImage:
                if (texture != null) rawImage.texture = texture;
                break;

            case TargetType.Image:
                if (texture != null) image.sprite = CreateSprite(image.sprite);
                PrepareGraphicMaterial();
                break;

            case TargetType.Text:
                if (texture != null && ShaderHasProperty(graphicMaterial, textTextureProperty))
                {
                    graphicMaterial.SetTexture(textTextureProperty, texture);
                }

                break;

            case TargetType.SpriteRenderer:
                // SpriteRenderer tự bind texture của sprite vào _MainTex lúc vẽ, nên đường vào
                // là sprite chứ không phải material.
                if (texture != null)
                {
                    SpriteRenderer spriteRenderer = (SpriteRenderer)targetRenderer;
                    spriteRenderer.sprite = CreateSprite(spriteRenderer.sprite);
                }

                PrepareRendererMaterial();
                break;

            case TargetType.MeshRenderer:
                PrepareRendererMaterial();

                if (texture != null && ShaderHasProperty(ownedMaterial, rendererTextureProperty))
                {
                    ownedMaterial.SetTexture(rendererTextureProperty, texture);
                }

                break;
        }
    }

    /// <summary>
    /// Tạo Sprite full-rect từ <see cref="texture"/>. Full rect là bắt buộc: mesh "tight" bị cắt
    /// theo silhouette nên phần ảnh cuộn tới sẽ bị xén mất.
    /// </summary>
    private Sprite CreateSprite(Sprite current)
    {
        float ppu = current != null ? current.pixelsPerUnit : Mathf.Max(0.01f, pixelsPerUnit);

        if (ownedSprite != null) Destroy(ownedSprite);

        ownedSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            ppu,
            0,
            SpriteMeshType.FullRect);

        ownedSprite.name = $"{texture.name} (Scroll_Texture)";
        return ownedSprite;
    }

    /// <summary>
    /// Image dùng chung material mặc định với mọi Image khác, sửa trực tiếp sẽ làm cả canvas
    /// cùng trôi. Vì vậy luôn tách ra một instance riêng.
    /// </summary>
    private void PrepareGraphicMaterial()
    {
        if (ownedMaterial != null) return;

        Material source = image.material != null ? image.material : image.defaultMaterial;

        ownedMaterial = new Material(source) { name = $"{source.name} (Scroll_Texture)" };
        image.material = ownedMaterial;
        graphicMaterial = ownedMaterial;
    }

    /// <summary>
    /// Đổi material khi shader hiện tại bỏ qua tiling/offset, hoặc khi cần một instance riêng
    /// để ghi texture vào (MeshRenderer).
    /// </summary>
    private void PrepareRendererMaterial()
    {
        if (ownedMaterial != null) return;

        Material source = targetRenderer.sharedMaterial;
        bool needsInstance = resolvedType == TargetType.MeshRenderer && texture != null;

        if (source == null)
        {
            ownedMaterial = new Material(GetFallbackShader());
            targetRenderer.material = ownedMaterial;
            return;
        }

        if (ShaderIgnoresScaleOffset(source.shader.name))
        {
            if (!autoFixMaterial)
            {
                Debug.LogWarning(
                    $"{nameof(Scroll_Texture)}: shader \"{source.shader.name}\" bỏ qua tiling/offset " +
                    "nên ảnh sẽ ĐỨNG YÊN, mà Auto Fix Material đang tắt. Bật nó lên hoặc đổi sang " +
                    "material dùng Unlit/Transparent.", this);
                return;
            }

            ownedMaterial = new Material(GetFallbackShader()) { name = "Scroll_Texture (auto)" };
            targetRenderer.material = ownedMaterial;

            Debug.Log(
                $"{nameof(Scroll_Texture)}: đã tự đổi material sang \"{ownedMaterial.shader.name}\" " +
                $"vì \"{source.shader.name}\" không áp dụng tiling/offset. Lưu ý shader Unlit bỏ qua " +
                "color tint và Flip X/Y của SpriteRenderer.", this);
            return;
        }

        if (!needsInstance) return;

        ownedMaterial = new Material(source) { name = $"{source.name} (Scroll_Texture)" };
        targetRenderer.material = ownedMaterial;
    }

    /// <summary>
    /// Các shader sprite dựng sẵn truyền thẳng UV mesh, không gọi TRANSFORM_TEX,
    /// nên _MainTex_ST hoàn toàn không có tác dụng.
    /// </summary>
    private static bool ShaderIgnoresScaleOffset(string shaderName)
    {
        return shaderName == "Sprites/Default" ||
               shaderName == "Sprites/Diffuse" ||
               shaderName.Contains("Sprite-Unlit-Default") ||
               shaderName.Contains("Sprite-Lit-Default");
    }

    private static Shader GetFallbackShader()
    {
        // Unlit/Transparent có TRANSFORM_TEX và giữ được alpha của sprite.
        return Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Texture");
    }

    #endregion

    #region APPLY

    /// <summary>Chạy mỗi frame nên chỉ đụng vào offset, tiling đã được set sẵn ở <see cref="ApplyTiling"/>.</summary>
    private void Apply()
    {
        // Chốt chặn cuối: không bao giờ ghi lên material khi shader không khai báo property,
        // vì mỗi lần ghi là một entry mồ côi làm hỏng Inspector của material.
        if (!hasTextureProperty) return;

        switch (resolvedType)
        {
            case TargetType.RawImage:
                // uvRect: (x, y) là offset, (width, height) là số lần lặp.
                rawImage.uvRect = new Rect(offset.x, offset.y, tiling.x, tiling.y);
                break;

            case TargetType.Image:
            case TargetType.Text:
                if (graphicMaterial == null) return;
                graphicMaterial.SetTextureOffset(texturePropertyId, offset);
                break;

            case TargetType.SpriteRenderer:
            case TargetType.MeshRenderer:
                // MaterialPropertyBlock không có SetTextureOffset, phải ghi nguyên vector _ST
                // nên tiling buộc phải kèm theo mỗi lần ghi.
                targetRenderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetVector(scaleOffsetPropertyId,
                    new Vector4(tiling.x, tiling.y, offset.x, offset.y));
                targetRenderer.SetPropertyBlock(propertyBlock);
                break;
        }
    }

    private void ApplyTiling()
    {
        if (!hasTextureProperty) return;
        if (resolvedType != TargetType.Image && resolvedType != TargetType.Text) return;
        if (graphicMaterial == null) return;

        graphicMaterial.SetTextureScale(texturePropertyId, tiling);
    }

    #endregion

    #region PUBLIC API

    public void SetSpeed(Vector2 value) => speed = value;

    public void SetTiling(Vector2 value)
    {
        tiling = value;

        Initialize();
        if (!isReady) return;

        ApplyTiling();
        Apply();
    }

    /// <summary>Đổi texture lúc runtime. Script tự gắn lại vào đúng chỗ của target hiện tại.</summary>
    public void SetTexture(Texture2D value)
    {
        texture = value;

        // Nếu chưa init thì Initialize() đã tự inject texture mới rồi, không cần làm lại.
        bool wasReady = isReady;
        Initialize();
        if (!isReady || !wasReady) return;

        InjectTexture();
        RefreshPropertySupport();
        Validate();
        ApplyTiling();
        Apply();
    }

    public void Pause() => IsScrolling = false;

    public void Resume() => IsScrolling = true;

    /// <summary>Đưa ảnh về vị trí ban đầu.</summary>
    public void ResetOffset()
    {
        offset = Vector2.zero;

        Initialize();
        if (isReady) Apply();
    }

    /// <summary>
    /// Gọi sau khi đổi font hoặc material của TMP lúc runtime:
    /// fontMaterial cũ đã bị thay nên tham chiếu đang cache không còn đúng.
    /// </summary>
    public void RefreshTarget()
    {
        if (resolvedType != TargetType.Text || text == null) return;

        graphicMaterial = text.fontMaterial;

        // Font material mới có thể dùng shader khác, phải kiểm tra lại property trước khi ghi.
        InjectTexture();
        RefreshPropertySupport();
        ApplyTiling();
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
        switch (resolvedType)
        {
            case TargetType.RawImage:
                ValidateTexture(rawImage.texture, "RawImage chưa có Texture");
                break;

            case TargetType.Image:
                ValidateTexture(image.sprite != null ? image.sprite.texture : null,
                    "Image chưa có Sprite và cũng chưa kéo Texture vào script");
                break;

            case TargetType.Text:
                ValidateText();
                break;

            case TargetType.SpriteRenderer:
                ValidateSpriteRenderer();
                break;

            case TargetType.MeshRenderer:
                ValidateMeshRenderer();
                break;
        }
    }

    private void ValidateText()
    {
        if (graphicMaterial == null)
        {
            Debug.LogWarning($"{nameof(Scroll_Texture)}: TMP chưa có font material.", this);
            return;
        }

        if (!hasTextureProperty)
        {
            Debug.LogWarning(
                $"{nameof(Scroll_Texture)}: shader \"{graphicMaterial.shader.name}\" không có " +
                $"property \"{textTextureProperty}\". Các shader TMP bản \"Mobile\" đã lược bỏ " +
                "Face Texture — hãy đổi material sang shader \"TextMeshPro/Distance Field\" " +
                "(bản đầy đủ).", this);
            return;
        }

        ValidateTexture(graphicMaterial.GetTexture(textTextureProperty),
            $"chưa có texture ở \"{textTextureProperty}\" (Face > Texture). Kéo Texture vào script " +
            "hoặc gán thẳng trong material");
    }

    private void ValidateSpriteRenderer()
    {
        SpriteRenderer spriteRenderer = (SpriteRenderer)targetRenderer;

        if (spriteRenderer.sprite == null)
        {
            Debug.LogWarning(
                $"{nameof(Scroll_Texture)}: SpriteRenderer chưa có Sprite và cũng chưa kéo Texture " +
                "vào script.", this);
            return;
        }

        if (texture == null && spriteRenderer.sprite.packed)
        {
            Debug.LogWarning(
                $"{nameof(Scroll_Texture)}: sprite \"{spriteRenderer.sprite.name}\" nằm trong " +
                "Sprite Atlas. UV của nó chỉ chiếm một phần atlas nên khi cuộn sẽ lòi sang " +
                "ảnh bên cạnh. Hãy để sprite này ngoài atlas.", this);
        }

        ValidateTexture(spriteRenderer.sprite.texture, null);
    }

    private void ValidateMeshRenderer()
    {
        Material material = ownedMaterial != null ? ownedMaterial : targetRenderer.sharedMaterial;

        if (material == null)
        {
            Debug.LogWarning($"{nameof(Scroll_Texture)}: MeshRenderer chưa có material.", this);
            return;
        }

        if (!hasTextureProperty)
        {
            Debug.LogWarning(
                $"{nameof(Scroll_Texture)}: shader \"{material.shader.name}\" không có property " +
                $"\"{rendererTextureProperty}\". URP Lit dùng \"_BaseMap\" — kiểm tra lại tên.", this);
            return;
        }

        ValidateTexture(material.GetTexture(rendererTextureProperty),
            "material chưa có texture và cũng chưa kéo Texture vào script");
    }

    /// <summary>Gộp hai lỗi hay gặp nhất: thiếu texture, và texture không để Wrap Mode = Repeat.</summary>
    private void ValidateTexture(Texture target, string missingMessage)
    {
        if (target == null)
        {
            if (!string.IsNullOrEmpty(missingMessage))
                Debug.LogWarning($"{nameof(Scroll_Texture)}: {missingMessage}.", this);
            return;
        }

        if (target.wrapMode == TextureWrapMode.Repeat) return;

        Debug.LogWarning(
            $"{nameof(Scroll_Texture)}: texture \"{target.name}\" đang để Wrap Mode = " +
            $"{target.wrapMode}. Đổi sang Repeat trong Import Settings, nếu không ảnh sẽ bị kéo " +
            "giãn ở mép thay vì lặp vô hạn.", this);
    }

    #endregion

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Cho phép chỉnh tiling/tốc độ và thấy kết quả ngay trong Inspector lúc đang Play.
        if (!Application.isPlaying || !isReady) return;
        ApplyTiling();
        Apply();
    }
#endif
}
