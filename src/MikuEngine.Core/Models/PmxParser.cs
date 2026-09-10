using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace MikuEngine.Core.Models;

/// <summary>
/// PMX 二进制文件解析器。忠实移植自 babylon-mmd 的 <c>pmxReader</c>（行序/偏移/类型完全一致）。
/// 纯托管实现，无平台依赖，可在单元测试中直接跑。
/// </summary>
public static class PmxParser
{
    /// <summary>
    /// 解析 PMX 数据。
    /// </summary>
    /// <param name="data">PMX 文件完整字节。</param>
    /// <returns>解析后的模型数据。</returns>
    /// <exception cref="FormatException">签名/版本非法或格式损坏。</exception>
    public static PmxModel Parse(byte[] data) => Parse(data, out _);

    /// <summary>
    /// 解析 PMX 数据，并返回解析后剩余的字节数。
    /// 剩余字节应恒为 0；非 0 说明存在未识别的扩展节/版本差异，用于测试校验结构完整性。
    /// </summary>
    public static PmxModel Parse(byte[] data, out int bytesRemaining)
    {
        var reader = new Reader(data);

        // --- 文件头 ---
        Span<byte> signature = stackalloc byte[3];
        reader.ReadBytes(signature);
        if (signature[0] != (byte)'P' || signature[1] != (byte)'M' || signature[2] != (byte)'X')
            throw new FormatException("不是 PMX 文件：签名非法。");

        reader.ReadByte(); // 跳过 1 字节对齐填充
        float version = reader.ReadSingle();
        byte globalsCount = reader.ReadByte();
        PmxEncoding encoding = (PmxEncoding)reader.ReadByte();
        byte additionalVec4Count = reader.ReadByte();
        byte vertexIndexSize = reader.ReadByte();
        byte textureIndexSize = reader.ReadByte();
        byte materialIndexSize = reader.ReadByte();
        byte boneIndexSize = reader.ReadByte();
        byte morphIndexSize = reader.ReadByte();
        byte rigidBodyIndexSize = reader.ReadByte();

        if (globalsCount < 8)
            throw new FormatException($"globalsCount 非法：{globalsCount}（小于 8）。");
        for (int i = 8; i < globalsCount; ++i)
            reader.ReadByte(); // 比 8 更多时跳过未知全局字节

        var header = new PmxHeader
        {
            Signature = "PMX",
            Version = version,
            Encoding = encoding,
            AdditionalVec4Count = additionalVec4Count,
            VertexIndexSize = vertexIndexSize,
            TextureIndexSize = textureIndexSize,
            MaterialIndexSize = materialIndexSize,
            BoneIndexSize = boneIndexSize,
            MorphIndexSize = morphIndexSize,
            RigidBodyIndexSize = rigidBodyIndexSize,
            ModelName = reader.ReadText(encoding),
            EnglishModelName = reader.ReadText(encoding),
            Comment = reader.ReadText(encoding),
            EnglishComment = reader.ReadText(encoding),
        };

        // 索引读取器（顶点无符号，其余有符号）
        var indices = new IndexReader(
            vertexIndexSize, textureIndexSize, materialIndexSize,
            boneIndexSize, morphIndexSize, rigidBodyIndexSize);

        var vertices = new PmxVertex[reader.ReadInt32()];
        for (int i = 0; i < vertices.Length; ++i)
            vertices[i] = ParseVertex(reader, indices, additionalVec4Count);

        var indexArr = new int[reader.ReadInt32()];
        for (int i = 0; i < indexArr.Length; ++i)
            indexArr[i] = indices.ReadVertex(reader);

        var textures = new string[reader.ReadInt32()];
        for (int i = 0; i < textures.Length; ++i)
            textures[i] = reader.ReadText(encoding);

        var materials = new PmxMaterial[reader.ReadInt32()];
        for (int i = 0; i < materials.Length; ++i)
            materials[i] = ParseMaterial(reader, indices, encoding);

        var bones = new PmxBone[reader.ReadInt32()];
        for (int i = 0; i < bones.Length; ++i)
            bones[i] = ParseBone(reader, indices, encoding);

        var morphs = new PmxMorph[reader.ReadInt32()];
        for (int i = 0; i < morphs.Length; ++i)
            morphs[i] = ParseMorph(reader, indices, encoding);

        var displayFrames = new PmxDisplayFrame[reader.ReadInt32()];
        for (int i = 0; i < displayFrames.Length; ++i)
            displayFrames[i] = ParseDisplayFrame(reader, indices, encoding);

        var rigidBodies = new PmxRigidBody[reader.ReadInt32()];
        for (int i = 0; i < rigidBodies.Length; ++i)
            rigidBodies[i] = ParseRigidBody(reader, indices, encoding);

        var joints = new PmxJoint[reader.ReadInt32()];
        for (int i = 0; i < joints.Length; ++i)
            joints[i] = ParseJoint(reader, indices, encoding);

        var softBodies = version <= 2.0f
            ? Array.Empty<PmxSoftBody>()
            : new PmxSoftBody[reader.ReadInt32()];
        if (version > 2.0f)
        {
            for (int i = 0; i < softBodies.Length; ++i)
                softBodies[i] = ParseSoftBody(reader, indices, encoding);
        }

        bytesRemaining = reader.BytesRemaining;

        return new PmxModel
        {
            Header = header,
            Vertices = vertices,
            Indices = indexArr,
            Textures = textures,
            Materials = materials,
            Bones = bones,
            Morphs = morphs,
            DisplayFrames = displayFrames,
            RigidBodies = rigidBodies,
            Joints = joints,
            SoftBodies = softBodies,
        };
    }

    private static PmxVertex ParseVertex(Reader r, IndexReader idx, byte additionalVec4Count)
    {
        Vector3 position = r.ReadVector3();
        Vector3 normal = r.ReadVector3();
        Vector2 uv = r.ReadVector2();

        var extra = additionalVec4Count == 0
            ? Array.Empty<Vector4>()
            : new Vector4[additionalVec4Count];
        for (int j = 0; j < additionalVec4Count; ++j)
            extra[j] = r.ReadVector4();

        var weightType = (PmxBoneWeightType)r.ReadByte();
        var w = ParseBoneWeight(r, idx, weightType);

        float edgeScale = r.ReadSingle();

        return new PmxVertex
        {
            Position = position,
            Normal = normal,
            Uv = uv,
            AdditionalVec4 = extra,
            Weight = w,
            EdgeScale = edgeScale,
        };
    }

    private static PmxBoneWeight ParseBoneWeight(Reader r, IndexReader idx, PmxBoneWeightType type)
    {
        switch (type)
        {
            case PmxBoneWeightType.Bdef1:
            {
                int b = idx.ReadBone(r);
                return new PmxBoneWeight
                {
                    Type = type, Bone0 = b, Bone1 = -1, Bone2 = -1, Bone3 = -1,
                    Weight0 = 1f, Weight1 = 0, Weight2 = 0, Weight3 = 0,
                    SdefC = Vector3.Zero, SdefR0 = Vector3.Zero, SdefR1 = Vector3.Zero,
                };
            }
            case PmxBoneWeightType.Bdef2:
            {
                int b0 = idx.ReadBone(r), b1 = idx.ReadBone(r);
                float w0 = r.ReadSingle();
                return new PmxBoneWeight
                {
                    Type = type, Bone0 = b0, Bone1 = b1, Bone2 = -1, Bone3 = -1,
                    Weight0 = w0, Weight1 = 0, Weight2 = 0, Weight3 = 0,
                    SdefC = Vector3.Zero, SdefR0 = Vector3.Zero, SdefR1 = Vector3.Zero,
                };
            }
            case PmxBoneWeightType.Bdef4:
            case PmxBoneWeightType.Qdef:
            {
                int b0 = idx.ReadBone(r), b1 = idx.ReadBone(r), b2 = idx.ReadBone(r), b3 = idx.ReadBone(r);
                float w0 = r.ReadSingle(), w1 = r.ReadSingle(), w2 = r.ReadSingle(), w3 = r.ReadSingle();
                return new PmxBoneWeight
                {
                    Type = type, Bone0 = b0, Bone1 = b1, Bone2 = b2, Bone3 = b3,
                    Weight0 = w0, Weight1 = w1, Weight2 = w2, Weight3 = w3,
                    SdefC = Vector3.Zero, SdefR0 = Vector3.Zero, SdefR1 = Vector3.Zero,
                };
            }
            case PmxBoneWeightType.Sdef:
            {
                int b0 = idx.ReadBone(r), b1 = idx.ReadBone(r);
                float w0 = r.ReadSingle();
                Vector3 c = r.ReadVector3();
                Vector3 r0 = r.ReadVector3();
                Vector3 r1 = r.ReadVector3();
                return new PmxBoneWeight
                {
                    Type = type, Bone0 = b0, Bone1 = b1, Bone2 = -1, Bone3 = -1,
                    Weight0 = w0, Weight1 = 0, Weight2 = 0, Weight3 = 0,
                    SdefC = c, SdefR0 = r0, SdefR1 = r1,
                };
            }
            default:
                throw new FormatException($"未知的顶点权重类型：{(int)type}");
        }
    }

    private static PmxMaterial ParseMaterial(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        Vector4 diffuse = r.ReadVector4();
        Vector3 specular = r.ReadVector3();
        float shininess = r.ReadSingle();
        Vector3 ambient = r.ReadVector3();
        var flag = (PmxMaterialFlag)r.ReadByte();
        Vector4 edgeColor = r.ReadVector4();
        float edgeSize = r.ReadSingle();
        int textureIndex = idx.ReadTexture(r);
        int sphereTextureIndex = idx.ReadTexture(r);
        var sphereMode = (PmxMaterialSphereMode)r.ReadByte();
        bool isSharedToon = r.ReadByte() == 1;
        int toonIndex = isSharedToon ? r.ReadByte() : idx.ReadTexture(r);
        string comment = r.ReadText(enc);
        int indexCount = r.ReadInt32();

        return new PmxMaterial
        {
            Name = name,
            EnglishName = engName,
            Diffuse = diffuse,
            Specular = specular,
            Shininess = shininess,
            Ambient = ambient,
            Flag = flag,
            EdgeColor = edgeColor,
            EdgeSize = edgeSize,
            TextureIndex = textureIndex,
            SphereTextureIndex = sphereTextureIndex,
            SphereTextureMode = sphereMode,
            IsSharedToonTexture = isSharedToon,
            ToonTextureIndex = toonIndex,
            Comment = comment,
            IndexCount = indexCount,
        };
    }

    private static PmxBone ParseBone(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        Vector3 position = r.ReadVector3();
        int parentIndex = idx.ReadBone(r);
        int transformOrder = r.ReadInt32();
        var flag = (PmxBoneFlag)r.ReadUInt16();

        int tailBoneIndex = -1;
        Vector3 tailPosition = Vector3.Zero;
        bool isTailBoneIndex = false;
        if ((flag & PmxBoneFlag.UseBoneIndexAsTailPosition) != 0)
        {
            tailBoneIndex = idx.ReadBone(r);
            isTailBoneIndex = true;
        }
        else
        {
            tailPosition = r.ReadVector3();
        }

        PmxAppendTransform? append = null;
        if ((flag & PmxBoneFlag.HasAppendMove) != 0 || (flag & PmxBoneFlag.HasAppendRotate) != 0)
        {
            append = new PmxAppendTransform { ParentIndex = idx.ReadBone(r), Ratio = r.ReadSingle() };
        }

        Vector3? axisLimit = (flag & PmxBoneFlag.HasAxisLimit) != 0 ? r.ReadVector3() : null;

        PmxLocalVector? localVector = null;
        if ((flag & PmxBoneFlag.HasLocalVector) != 0)
        {
            localVector = new PmxLocalVector { X = r.ReadVector3(), Z = r.ReadVector3() };
        }

        int? externalParent = (flag & PmxBoneFlag.IsExternalParentTransformed) != 0 ? r.ReadInt32() : null;

        PmxIk? ik = null;
        if ((flag & PmxBoneFlag.IsIkEnabled) != 0)
        {
            int target = idx.ReadBone(r);
            int iteration = r.ReadInt32();
            float rotationConstraint = r.ReadSingle();
            var links = new PmxIkLink[r.ReadInt32()];
            for (int i = 0; i < links.Length; ++i)
            {
                int linkBone = idx.ReadBone(r);
                bool hasLimit = r.ReadByte() == 1;
                Vector3 min = Vector3.Zero, max = Vector3.Zero;
                if (hasLimit)
                {
                    min = r.ReadVector3();
                    max = r.ReadVector3();
                }
                links[i] = new PmxIkLink
                {
                    BoneIndex = linkBone,
                    HasLimitation = hasLimit,
                    MinimumAngle = min,
                    MaximumAngle = max,
                };
            }
            ik = new PmxIk { Target = target, Iteration = iteration, RotationConstraint = rotationConstraint, Links = links };
        }

        return new PmxBone
        {
            Name = name,
            EnglishName = engName,
            Position = position,
            ParentBoneIndex = parentIndex,
            TransformOrder = transformOrder,
            Flag = flag,
            TailBoneIndex = tailBoneIndex,
            TailPosition = tailPosition,
            IsTailBoneIndex = isTailBoneIndex,
            AppendTransform = append,
            AxisLimit = axisLimit,
            LocalVector = localVector,
            ExternalParentIndex = externalParent,
            Ik = ik,
        };
    }

    private static PmxMorph ParseMorph(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        var category = (PmxMorphCategory)r.ReadByte();
        var type = (PmxMorphType)r.ReadByte();
        int count = r.ReadInt32();

        switch (type)
        {
            case PmxMorphType.Group:
            case PmxMorphType.Flip:
            {
                var indexArr = new int[count];
                var ratios = new float[count];
                for (int i = 0; i < count; ++i)
                {
                    indexArr[i] = idx.ReadMorph(r);
                    ratios[i] = r.ReadSingle();
                }
                return new PmxMorph { Name = name, EnglishName = engName, Category = category, Type = type, Indices = indexArr, Ratios = ratios };
            }
            case PmxMorphType.Vertex:
            {
                var indexArr = new int[count];
                var positions = new float[count * 3];
                for (int i = 0; i < count; ++i)
                {
                    indexArr[i] = idx.ReadVertex(r);
                    positions[i * 3 + 0] = r.ReadSingle();
                    positions[i * 3 + 1] = r.ReadSingle();
                    positions[i * 3 + 2] = r.ReadSingle();
                }
                return new PmxMorph { Name = name, EnglishName = engName, Category = category, Type = type, Indices = indexArr, Positions = positions };
            }
            case PmxMorphType.Bone:
            {
                var indexArr = new int[count];
                var positions = new float[count * 3];
                var rotations = new float[count * 4];
                for (int i = 0; i < count; ++i)
                {
                    indexArr[i] = idx.ReadBone(r);
                    positions[i * 3 + 0] = r.ReadSingle();
                    positions[i * 3 + 1] = r.ReadSingle();
                    positions[i * 3 + 2] = r.ReadSingle();
                    rotations[i * 4 + 0] = r.ReadSingle();
                    rotations[i * 4 + 1] = r.ReadSingle();
                    rotations[i * 4 + 2] = r.ReadSingle();
                    rotations[i * 4 + 3] = r.ReadSingle();
                }
                return new PmxMorph { Name = name, EnglishName = engName, Category = category, Type = type, Indices = indexArr, Positions = positions, Rotations = rotations };
            }
            case PmxMorphType.Uv:
            case PmxMorphType.AdditionalUv1:
            case PmxMorphType.AdditionalUv2:
            case PmxMorphType.AdditionalUv3:
            case PmxMorphType.AdditionalUv4:
            {
                var indexArr = new int[count];
                var offsets = new float[count * 4];
                for (int i = 0; i < count; ++i)
                {
                    indexArr[i] = idx.ReadVertex(r);
                    offsets[i * 4 + 0] = r.ReadSingle();
                    offsets[i * 4 + 1] = r.ReadSingle();
                    offsets[i * 4 + 2] = r.ReadSingle();
                    offsets[i * 4 + 3] = r.ReadSingle();
                }
                return new PmxMorph { Name = name, EnglishName = engName, Category = category, Type = type, Indices = indexArr, Offsets = offsets };
            }
            case PmxMorphType.Material:
            {
                var elements = new PmxMaterialMorphElement[count];
                for (int i = 0; i < count; ++i)
                {
                    elements[i] = new PmxMaterialMorphElement
                    {
                        MaterialIndex = idx.ReadMaterial(r),
                        Type = (PmxMaterialMorphType)r.ReadByte(),
                        Diffuse = r.ReadVector4(),
                        Specular = r.ReadVector3(),
                        Shininess = r.ReadSingle(),
                        Ambient = r.ReadVector3(),
                        EdgeColor = r.ReadVector4(),
                        EdgeSize = r.ReadSingle(),
                        TextureColor = r.ReadVector4(),
                        SphereTextureColor = r.ReadVector4(),
                        ToonTextureColor = r.ReadVector4(),
                    };
                }
                return new PmxMorph { Name = name, EnglishName = engName, Category = category, Type = type, MaterialElements = elements };
            }
            case PmxMorphType.Impulse:
            {
                var indexArr = new int[count];
                var isLocals = new bool[count];
                var velocities = new float[count * 3];
                var torques = new float[count * 3];
                for (int i = 0; i < count; ++i)
                {
                    indexArr[i] = idx.ReadRigidBody(r);
                    isLocals[i] = r.ReadByte() == 1;
                    velocities[i * 3 + 0] = r.ReadSingle();
                    velocities[i * 3 + 1] = r.ReadSingle();
                    velocities[i * 3 + 2] = r.ReadSingle();
                    torques[i * 3 + 0] = r.ReadSingle();
                    torques[i * 3 + 1] = r.ReadSingle();
                    torques[i * 3 + 2] = r.ReadSingle();
                }
                return new PmxMorph { Name = name, EnglishName = engName, Category = category, Type = type, Indices = indexArr, IsLocals = isLocals, Velocities = velocities, Torques = torques };
            }
            default:
                throw new FormatException($"未知的表情类型：{(int)type}");
        }
    }

    private static PmxDisplayFrame ParseDisplayFrame(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        bool isSpecial = r.ReadByte() == 1;
        var elements = new PmxDisplayFrameElement[r.ReadInt32()];
        for (int i = 0; i < elements.Length; ++i)
        {
            var type = (PmxDisplayFrameElementType)r.ReadByte();
            int index = type == PmxDisplayFrameElementType.Bone
                ? idx.ReadBone(r)
                : idx.ReadMorph(r);
            elements[i] = new PmxDisplayFrameElement { Type = type, Index = index };
        }
        return new PmxDisplayFrame { Name = name, EnglishName = engName, IsSpecialFrame = isSpecial, Elements = elements };
    }

    private static PmxRigidBody ParseRigidBody(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        int boneIndex = idx.ReadBone(r);
        byte collisionGroup = r.ReadByte();
        ushort collisionMask = r.ReadUInt16();
        var shapeType = (PmxRigidBodyShapeType)r.ReadByte();
        Vector3 size = r.ReadVector3();
        Vector3 position = r.ReadVector3();
        Vector3 rotation = r.ReadVector3();
        float mass = r.ReadSingle();
        float linearDamping = r.ReadSingle();
        float angularDamping = r.ReadSingle();
        float repulsion = r.ReadSingle();
        float friction = r.ReadSingle();
        var mode = (PmxRigidBodyMode)r.ReadByte();

        return new PmxRigidBody
        {
            Name = name,
            EnglishName = engName,
            BoneIndex = boneIndex,
            CollisionGroup = collisionGroup,
            CollisionMask = collisionMask,
            ShapeType = shapeType,
            ShapeSize = size,
            ShapePosition = position,
            ShapeRotation = rotation,
            Mass = mass,
            LinearDamping = linearDamping,
            AngularDamping = angularDamping,
            Repulsion = repulsion,
            Friction = friction,
            PhysicsMode = mode,
        };
    }

    private static PmxJoint ParseJoint(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        var type = (PmxJointType)r.ReadByte();
        int a = idx.ReadRigidBody(r);
        int b = idx.ReadRigidBody(r);
        Vector3 position = r.ReadVector3();
        Vector3 rotation = r.ReadVector3();
        Vector3 positionMin = r.ReadVector3();
        Vector3 positionMax = r.ReadVector3();
        Vector3 rotationMin = r.ReadVector3();
        Vector3 rotationMax = r.ReadVector3();
        Vector3 springPosition = r.ReadVector3();
        Vector3 springRotation = r.ReadVector3();

        return new PmxJoint
        {
            Name = name,
            EnglishName = engName,
            Type = type,
            RigidBodyIndexA = a,
            RigidBodyIndexB = b,
            Position = position,
            Rotation = rotation,
            PositionMin = positionMin,
            PositionMax = positionMax,
            RotationMin = rotationMin,
            RotationMax = rotationMax,
            SpringPosition = springPosition,
            SpringRotation = springRotation,
        };
    }

    private static PmxSoftBody ParseSoftBody(Reader r, IndexReader idx, PmxEncoding enc)
    {
        string name = r.ReadText(enc);
        string engName = r.ReadText(enc);
        var type = (PmxSoftBodyType)r.ReadByte();
        int materialIndex = idx.ReadMaterial(r);
        byte collisionGroup = r.ReadByte();
        ushort collisionMask = r.ReadUInt16();
        byte flags = r.ReadByte();
        int bLinkDistance = r.ReadInt32();
        int clusterCount = r.ReadInt32();
        float totalMass = r.ReadSingle();
        float collisionMargin = r.ReadSingle();
        int aeroModel = r.ReadInt32();

        var config = new PmxSoftBodyConfig
        {
            Vcf = r.ReadSingle(), Dp = r.ReadSingle(), Dg = r.ReadSingle(), Lf = r.ReadSingle(),
            Pr = r.ReadSingle(), Vc = r.ReadSingle(), Df = r.ReadSingle(), Mt = r.ReadSingle(),
            Chr = r.ReadSingle(), Khr = r.ReadSingle(), Shr = r.ReadSingle(), Ahr = r.ReadSingle(),
        };
        var cluster = new float[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
        var iteration = new int[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
        var material = new int[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };

        var anchors = new PmxSoftBodyAnchor[r.ReadInt32()];
        for (int i = 0; i < anchors.Length; ++i)
        {
            anchors[i] = new PmxSoftBodyAnchor
            {
                RigidBodyIndex = idx.ReadRigidBody(r),
                VertexIndex = idx.ReadVertex(r),
                IsNearMode = r.ReadByte() != 0,
            };
        }

        var vertexPins = new int[r.ReadInt32()];
        for (int i = 0; i < vertexPins.Length; ++i)
            vertexPins[i] = idx.ReadVertex(r);

        return new PmxSoftBody
        {
            Name = name,
            EnglishName = engName,
            Type = type,
            MaterialIndex = materialIndex,
            CollisionGroup = collisionGroup,
            CollisionMask = collisionMask,
            Flags = flags,
            BLinkDistance = bLinkDistance,
            ClusterCount = clusterCount,
            TotalMass = totalMass,
            CollisionMargin = collisionMargin,
            AeroModel = aeroModel,
            Config = config,
            Cluster = cluster,
            Iteration = iteration,
            Material = material,
            Anchors = anchors,
            VertexPins = vertexPins,
        };
    }

    /// <summary>
    /// 各索引宽度的读取器。顶点索引为无符号 8/16 位或 32 位有符号；
    /// 其它（纹理/材质/骨骼/表情/刚体）为有符号 8/16/32 位。
    /// 与 babylon-mmd 的 <c>IndexReader</c> 完全一致。
    /// </summary>
    private ref struct IndexReader
    {
        private readonly int _vertex, _texture, _material, _bone, _morph, _rigidBody;

        public IndexReader(
            int vertex, int texture, int material,
            int bone, int morph, int rigidBody)
        {
            _vertex = vertex;
            _texture = texture;
            _material = material;
            _bone = bone;
            _morph = morph;
            _rigidBody = rigidBody;
        }

        public int ReadVertex(Reader r) => _vertex switch
        {
            1 => r.ReadByte(),
            2 => r.ReadUInt16(),
            4 => r.ReadInt32(),
            _ => throw new FormatException($"非法的顶点索引宽度：{_vertex}"),
        };

        public int ReadTexture(Reader r) => ReadSigned(r, _texture, "纹理");
        public int ReadMaterial(Reader r) => ReadSigned(r, _material, "材质");
        public int ReadBone(Reader r) => ReadSigned(r, _bone, "骨骼");
        public int ReadMorph(Reader r) => ReadSigned(r, _morph, "表情");
        public int ReadRigidBody(Reader r) => ReadSigned(r, _rigidBody, "刚体");

        private static int ReadSigned(Reader r, int size, string what) => size switch
        {
            1 => r.ReadSByte(),
            2 => r.ReadInt16(),
            4 => r.ReadInt32(),
            _ => throw new FormatException($"非法的 {what} 索引宽度：{size}"),
        };
    }

    /// <summary>
    /// 小端二进制读取器 + 文本解码。
    /// </summary>
    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _pos;

        public Reader(byte[] data)
        {
            _data = data;
            _pos = 0;
        }

        public int BytesRemaining => _data.Length - _pos;

        public void ReadBytes(Span<byte> dest)
        {
            _data.AsSpan(_pos, dest.Length).CopyTo(dest);
            _pos += dest.Length;
        }

        public byte ReadByte()
        {
            return _data[_pos++];
        }

        public sbyte ReadSByte()
        {
            return unchecked((sbyte)_data[_pos++]);
        }

        public ushort ReadUInt16()
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos, 2));
            _pos += 2;
            return v;
        }

        public short ReadInt16()
        {
            short v = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(_pos, 2));
            _pos += 2;
            return v;
        }

        public uint ReadUInt32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public int ReadInt32()
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public float ReadSingle()
        {
            float v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos, 4)));
            _pos += 4;
            return v;
        }

        public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());
        public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
        public Vector4 ReadVector4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

        /// <summary>
        /// 读取前置 int32 长度的字符串。UTF-8 用 UTF8 解码；其余（含 ShiftJis）按 UTF-16LE 解码。
        /// 与 babylon-mmd 一致：非 UTF-8 一律按 UTF-16LE 处理（ShiftJis 不支持在此简化为 UTF-16LE）。
        /// </summary>
        public string ReadText(PmxEncoding encoding)
        {
            int length = ReadInt32();
            ReadOnlySpan<byte> bytes = _data.AsSpan(_pos, length);
            _pos += length;

            var textEncoding = encoding == PmxEncoding.Utf8 ? Encoding.UTF8 : Encoding.Unicode;
            return textEncoding.GetString(bytes);
        }
    }
}