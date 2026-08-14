using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nanto.WinRtAppearanceAbiGen;

internal static class Generator
{
    private const string DefaultAttribute = "Windows.Foundation.Metadata.DefaultAttribute";
    private const string GuidAttribute = "Windows.Foundation.Metadata.GuidAttribute";
    private const string SourceFileName = "WinRtAppearanceInterop.g.cs";
    private const string ManifestFileName = "winrt-appearance-interop-manifest.json";
    private const string ParameterizedInterfaceNamespace = "11f47ad5-7b73-42c0-abae-878b1e16adee";

    public static void Generate(GeneratorArguments arguments)
    {
        byte[] specBytes = File.ReadAllBytes(arguments.SpecPath);
        AppearanceSpecification specification = JsonSerializer.Deserialize(specBytes, GeneratorJsonContext.Default.AppearanceSpecification)
            ?? throw new InvalidDataException("The projection specification is empty.");
        ValidateSpecification(specification);

        string packageVersion = Path.GetFileName(Path.TrimEndingDirectorySeparator(arguments.PackageRoot));
        if (!string.Equals(packageVersion, specification.TargetingPackVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The resolved targeting pack version '{packageVersion}' does not match '{specification.TargetingPackVersion}'.");
        }

        string winmdPath = Path.Combine(arguments.PackageRoot, "winmd", specification.Contract);
        if (!File.Exists(winmdPath))
        {
            throw new FileNotFoundException($"The selected WinMD contract does not exist: {winmdPath}", winmdPath);
        }

        byte[] winmdBytes = File.ReadAllBytes(winmdPath);
        string foundationWinmdPath = Path.Combine(arguments.PackageRoot, "winmd", specification.FoundationContract);
        if (!File.Exists(foundationWinmdPath))
        {
            throw new FileNotFoundException($"The selected foundation WinMD contract does not exist: {foundationWinmdPath}", foundationWinmdPath);
        }

        byte[] foundationWinmdBytes = File.ReadAllBytes(foundationWinmdPath);
        string projectionRuntimePath = Path.Combine(arguments.PackageRoot, specification.ProjectionRuntime);
        if (!File.Exists(projectionRuntimePath))
        {
            throw new FileNotFoundException($"The selected projection runtime does not exist: {projectionRuntimePath}", projectionRuntimePath);
        }

        byte[] projectionRuntimeBytes = File.ReadAllBytes(projectionRuntimePath);
        Projection projection = ReadProjection(winmdBytes, foundationWinmdBytes, projectionRuntimeBytes, specification);
        string source = EmitSource(projection);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        GenerationManifest manifest = new()
        {
            TargetFramework = specification.TargetFramework,
            TargetingPackVersion = specification.TargetingPackVersion,
            Inputs =
            [
                new($"Microsoft.Windows.SDK.NET.Ref/{packageVersion}/winmd/{specification.Contract}", Hash(winmdBytes)),
                new($"Microsoft.Windows.SDK.NET.Ref/{packageVersion}/winmd/{specification.FoundationContract}", Hash(foundationWinmdBytes)),
                new($"Microsoft.Windows.SDK.NET.Ref/{packageVersion}/{specification.ProjectionRuntime}", Hash(projectionRuntimeBytes)),
                new("eng/spikes/Nanto.WinRtAppearanceAbi/winrt-appearance-spec.json", Hash(specBytes)),
            ],
            RuntimeClass = new(
                specification.RuntimeClass,
                projection.DefaultInterfaceName,
                projection.DefaultInterfaceIid.ToString("D")),
            Interface = new(
                projection.InterfaceName,
                projection.InterfaceIid.ToString("D"),
                projection.Methods.Select(
                    (name, index) => new ManifestSlot(name, index + 6, projection.MethodSignatures[index])).ToArray()),
            EventHandler = new(specification.EventHandler, projection.EventHandlerSignature, projection.EventHandlerIid.ToString("D")),
            Output = new(
                "eng/spikes/Nanto.WinRtAppearanceAbi/Generated/WinRtAppearanceInterop.g.cs",
                sourceBytes.Length,
                Hash(sourceBytes),
                "utf-8",
                "lf"),
        };
        byte[] manifestBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(manifest, GeneratorJsonContext.Default.GenerationManifest).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

        Directory.CreateDirectory(arguments.OutputDirectory);
        Write(Path.Combine(arguments.OutputDirectory, SourceFileName), sourceBytes);
        Write(Path.Combine(arguments.OutputDirectory, ManifestFileName), manifestBytes);
        if (arguments.VerifyDirectory is not null)
        {
            Verify(Path.Combine(arguments.OutputDirectory, SourceFileName), Path.Combine(arguments.VerifyDirectory, SourceFileName));
            Verify(Path.Combine(arguments.OutputDirectory, ManifestFileName), Path.Combine(arguments.VerifyDirectory, ManifestFileName));
        }
    }

    private static Projection ReadProjection(
        byte[] winmdBytes,
        byte[] foundationWinmdBytes,
        byte[] projectionRuntimeBytes,
        AppearanceSpecification specification)
    {
        using MemoryStream stream = new(winmdBytes, writable: false);
        using PEReader peReader = new(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        using MemoryStream foundationStream = new(foundationWinmdBytes, writable: false);
        using PEReader foundationPeReader = new(foundationStream);
        MetadataReader foundationReader = foundationPeReader.GetMetadataReader();
        using MemoryStream projectionRuntimeStream = new(projectionRuntimeBytes, writable: false);
        using PEReader projectionRuntimePeReader = new(projectionRuntimeStream);
        MetadataReader projectionRuntimeReader = projectionRuntimePeReader.GetMetadataReader();
        TypeDefinition runtimeClass = reader.GetTypeDefinition(FindType(reader, specification.RuntimeClass));
        TypeDefinition settingsInterface = reader.GetTypeDefinition(FindType(reader, specification.Interface));
        TypeDefinition color = reader.GetTypeDefinition(FindType(reader, "Windows.UI.Color"));
        TypeDefinition colorType = reader.GetTypeDefinition(FindType(reader, "Windows.UI.ViewManagement.UIColorType"));
        TypeDefinition typedEventHandler = foundationReader.GetTypeDefinition(FindType(foundationReader, "Windows.Foundation.TypedEventHandler`2"));

        Guid interfaceIid = ReadGuid(reader, settingsInterface);
        (string defaultInterfaceName, Guid defaultInterfaceIid) = ReadRuntimeClassClosure(reader, runtimeClass, specification.Interface);
        Guid typedEventHandlerIid = ReadGuid(foundationReader, typedEventHandler);
        string[] methods = settingsInterface.GetMethods()
            .Select(handle => reader.GetString(reader.GetMethodDefinition(handle).Name))
            .ToArray();
        if (!methods.SequenceEqual(specification.Members, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The selected interface method order is '{string.Join(", ", methods)}', expected '{string.Join(", ", specification.Members)}'.");
        }

        string[] methodSignatures = ValidateMethodSignatures(reader, settingsInterface);
        ValidateFields(reader, color, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "System.Byte",
            ["R"] = "System.Byte",
            ["G"] = "System.Byte",
            ["B"] = "System.Byte",
        });
        TypeDefinition eventToken = projectionRuntimeReader.GetTypeDefinition(FindType(projectionRuntimeReader, "WinRT.EventRegistrationToken"));
        ValidateFields(projectionRuntimeReader, eventToken, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Value"] = "System.Int64",
        });
        ValidateEnum(reader, colorType, new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Background"] = 0,
            ["Foreground"] = 1,
            ["AccentDark3"] = 2,
            ["AccentDark2"] = 3,
            ["AccentDark1"] = 4,
            ["Accent"] = 5,
            ["AccentLight1"] = 6,
            ["AccentLight2"] = 7,
            ["AccentLight3"] = 8,
            ["Complement"] = 9,
        });

        string signature = $"pinterface({{{typedEventHandlerIid:D}}};rc({specification.RuntimeClass};{{{defaultInterfaceIid:D}}});cinterface(IInspectable))";
        Guid eventHandlerIid = CreateParameterizedIid(signature);
        return new Projection(
            defaultInterfaceName,
            defaultInterfaceIid,
            specification.Interface,
            interfaceIid,
            methods,
            methodSignatures,
            signature,
            eventHandlerIid);
    }

    private static void ValidateSpecification(AppearanceSpecification specification)
    {
        if (specification.SchemaVersion != 1
            || specification.TargetFramework != "net10.0-windows10.0.19041.0"
            || specification.TargetingPackVersion != "10.0.19041.57"
            || specification.Contract != "Windows.Foundation.UniversalApiContract.winmd"
            || specification.FoundationContract != "Windows.Foundation.FoundationContract.winmd"
            || specification.ProjectionRuntime != "lib/net8.0/WinRT.Runtime.dll"
            || specification.RuntimeClass != "Windows.UI.ViewManagement.UISettings"
            || specification.Interface != "Windows.UI.ViewManagement.IUISettings3"
            || specification.EventHandler != "Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.UISettings, System.Object>"
            || !specification.Members.SequenceEqual(
                ["GetColorValue", "add_ColorValuesChanged", "remove_ColorValuesChanged"],
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("The WinRT appearance projection specification is unsupported.");
        }
    }

    private static TypeDefinitionHandle FindType(MetadataReader reader, string fullName)
    {
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string candidate = $"{reader.GetString(definition.Namespace)}.{reader.GetString(definition.Name)}";
            if (candidate == fullName)
            {
                return handle;
            }
        }

        throw new InvalidDataException($"Required WinMD type '{fullName}' was not found.");
    }

    private static (string Name, Guid Iid) ReadRuntimeClassClosure(
        MetadataReader reader,
        TypeDefinition runtimeClass,
        string selectedInterfaceName)
    {
        (string Name, Guid Iid)? defaultInterface = null;
        bool selectedInterfaceFound = false;
        foreach (InterfaceImplementationHandle handle in runtimeClass.GetInterfaceImplementations())
        {
            InterfaceImplementation implementation = reader.GetInterfaceImplementation(handle);
            TypeDefinition definition = ResolveTypeDefinition(reader, implementation.Interface);
            string name = GetFullName(reader, definition);
            selectedInterfaceFound |= name == selectedInterfaceName;
            if (HasAttribute(reader, implementation.GetCustomAttributes(), DefaultAttribute))
            {
                if (defaultInterface is not null)
                {
                    throw new InvalidDataException("UISettings declares more than one default interface in the selected WinMD.");
                }

                defaultInterface = (name, ReadGuid(reader, definition));
            }
        }

        if (!selectedInterfaceFound)
        {
            throw new InvalidDataException($"UISettings does not implement selected interface '{selectedInterfaceName}'.");
        }

        return defaultInterface
            ?? throw new InvalidDataException("UISettings does not declare a default interface in the selected WinMD.");
    }

    private static TypeDefinition ResolveTypeDefinition(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            return reader.GetTypeDefinition((TypeDefinitionHandle)handle);
        }

        if (handle.Kind == HandleKind.TypeReference)
        {
            TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)handle);
            return reader.GetTypeDefinition(FindType(reader, GetFullName(reader, reference)));
        }

        throw new InvalidDataException("A WinMD interface implementation uses an unsupported type handle.");
    }

    private static Guid ReadGuid(MetadataReader reader, TypeDefinition definition)
    {
        foreach (CustomAttributeHandle handle in definition.GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (GetAttributeTypeName(reader, attribute.Constructor) != GuidAttribute)
            {
                continue;
            }

            BlobReader value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                break;
            }

            return new Guid(
                value.ReadUInt32(),
                value.ReadUInt16(),
                value.ReadUInt16(),
                value.ReadByte(),
                value.ReadByte(),
                value.ReadByte(),
                value.ReadByte(),
                value.ReadByte(),
                value.ReadByte(),
                value.ReadByte(),
                value.ReadByte());
        }

        throw new InvalidDataException($"WinMD type '{reader.GetString(definition.Name)}' has no GUID.");
    }

    private static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection handles, string name) =>
        handles.Any(handle => GetAttributeTypeName(reader, reader.GetCustomAttribute(handle).Constructor) == name);

    private static string GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle parent = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => throw new InvalidDataException("A WinMD custom attribute uses an unsupported constructor handle."),
        };
        return parent.Kind switch
        {
            HandleKind.TypeReference => GetFullName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
            HandleKind.TypeDefinition => GetFullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)parent)),
            _ => throw new InvalidDataException("A WinMD custom attribute uses an unsupported declaring type."),
        };
    }

    private static string GetFullName(MetadataReader reader, TypeReference type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string GetFullName(MetadataReader reader, TypeDefinition type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string[] ValidateMethodSignatures(MetadataReader reader, TypeDefinition definition)
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["GetColorValue"] = "Windows.UI.Color (Windows.UI.ViewManagement.UIColorType)",
            ["add_ColorValuesChanged"] =
                "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken (Windows.Foundation.TypedEventHandler`2<Windows.UI.ViewManagement.UISettings,System.Object>)",
            ["remove_ColorValuesChanged"] =
                "System.Void (System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken)",
        };
        SignatureTypeProvider provider = new();
        List<string> signatures = [];
        foreach (MethodDefinitionHandle handle in definition.GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            string name = reader.GetString(method.Name);
            MethodSignature<string> signature = method.DecodeSignature(provider, genericContext: null);
            string actual = $"{signature.ReturnType} ({string.Join(',', signature.ParameterTypes)})";
            if (!expected.TryGetValue(name, out string? expectedSignature) || actual != expectedSignature)
            {
                throw new InvalidDataException($"WinMD method '{name}' signature is '{actual}', expected '{expectedSignature}'.");
            }

            signatures.Add(actual);
        }

        return [.. signatures];
    }

    private static void ValidateFields(MetadataReader reader, TypeDefinition definition, Dictionary<string, string> expected)
    {
        SignatureTypeProvider provider = new();
        Dictionary<string, string> actual = new(StringComparer.Ordinal);
        foreach (FieldDefinitionHandle handle in definition.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(handle);
            string name = reader.GetString(field.Name);
            if (name != "value__")
            {
                actual.Add(name, field.DecodeSignature(provider, genericContext: null));
            }
        }

        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out string? type) || type != pair.Value))
        {
            throw new InvalidDataException($"WinMD type '{reader.GetString(definition.Name)}' fields do not match the reviewed layout.");
        }
    }

    private static void ValidateEnum(MetadataReader reader, TypeDefinition definition, Dictionary<string, int> expected)
    {
        Dictionary<string, int> actual = new(StringComparer.Ordinal);
        foreach (FieldDefinitionHandle handle in definition.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(handle);
            if (reader.GetString(field.Name) == "value__")
            {
                continue;
            }

            Constant constant = reader.GetConstant(field.GetDefaultValue());
            BlobReader value = reader.GetBlobReader(constant.Value);
            actual.Add(reader.GetString(field.Name), value.ReadInt32());
        }

        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out int value) || value != pair.Value))
        {
            throw new InvalidDataException($"WinMD enum '{reader.GetString(definition.Name)}' does not match the reviewed values.");
        }
    }

    private static Guid CreateParameterizedIid(string signature)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        _ = Guid.Parse(ParameterizedInterfaceNamespace).TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        byte[] signatureBytes = Encoding.UTF8.GetBytes(signature);
        byte[] input = new byte[namespaceBytes.Length + signatureBytes.Length];
        namespaceBytes.CopyTo(input);
        signatureBytes.CopyTo(input.AsSpan(namespaceBytes.Length));
        Span<byte> hash = stackalloc byte[20];
        // The Windows Runtime parameterized-interface IID algorithm mandates SHA-1 as a UUID derivation primitive, not for security.
#pragma warning disable CA5350
        _ = SHA1.HashData(input, hash);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    private static string EmitSource(Projection projection)
    {
        string source = $$"""
            // <auto-generated />
            #nullable enable

            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

            namespace Nanto.WinRtAppearanceAbiSpike;

            internal enum RawUIColorType
            {
                Background = 0,
                Foreground = 1,
                AccentDark3 = 2,
                AccentDark2 = 3,
                AccentDark1 = 4,
                Accent = 5,
                AccentLight1 = 6,
                AccentLight2 = 7,
                AccentLight3 = 8,
                Complement = 9,
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct RawColor
            {
                internal byte A;
                internal byte R;
                internal byte G;
                internal byte B;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal readonly struct RawEventRegistrationToken(long value)
            {
                internal long Value { get; } = value;
            }

            internal static unsafe class RawWinRtAbi
            {
                internal static readonly Guid IUnknownIid = new("00000000-0000-0000-c000-000000000046");
                internal static readonly Guid IInspectableIid = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
                internal static readonly Guid UISettings3Iid = new("{{projection.InterfaceIid:D}}");
                internal static readonly Guid ColorValuesChangedHandlerIid = new("{{projection.EventHandlerIid:D}}");

                internal static int QueryInterface(nint instance, in Guid iid, out nint result)
                {
                    result = 0;
                    fixed (Guid* iidPointer = &iid)
                    fixed (nint* resultPointer = &result)
                    {
                        nint* vtable = *(nint**)instance;
                        return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0])(instance, iidPointer, resultPointer);
                    }
                }

                internal static uint Release(nint instance)
                {
                    nint* vtable = *(nint**)instance;
                    return ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(instance);
                }

                internal static int GetColorValue(nint settings, RawUIColorType desiredColor, out RawColor value)
                {
                    value = default;
                    fixed (RawColor* valuePointer = &value)
                    {
                        nint* vtable = *(nint**)settings;
                        return ((delegate* unmanaged[Stdcall]<nint, RawUIColorType, RawColor*, int>)vtable[6])(settings, desiredColor, valuePointer);
                    }
                }

                internal static int AddColorValuesChanged(nint settings, nint handler, out RawEventRegistrationToken token)
                {
                    token = default;
                    fixed (RawEventRegistrationToken* tokenPointer = &token)
                    {
                        nint* vtable = *(nint**)settings;
                        return ((delegate* unmanaged[Stdcall]<nint, nint, RawEventRegistrationToken*, int>)vtable[7])(settings, handler, tokenPointer);
                    }
                }

                internal static int RemoveColorValuesChanged(nint settings, RawEventRegistrationToken token)
                {
                    nint* vtable = *(nint**)settings;
                    return ((delegate* unmanaged[Stdcall]<nint, RawEventRegistrationToken, int>)vtable[8])(settings, token);
                }
            }

            internal sealed unsafe class RawColorValuesChangedHandler : IDisposable
            {
                private const int ENoInterface = unchecked((int)0x80004002);
                private const int EPointer = unchecked((int)0x80004003);
                private const int EFail = unchecked((int)0x80004005);

                private readonly Action _callback;
                private static int _activeInstances;
                private nint _instance;
                private int _disposed;
                private int _suppressed;

                internal nint Pointer => Volatile.Read(ref _instance);

                internal static int ActiveInstances => Volatile.Read(ref _activeInstances);

                internal RawColorValuesChangedHandler(Action callback)
                {
                    ArgumentNullException.ThrowIfNull(callback);
                    _callback = callback;
                    Instance* instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
                    instance->Vtable = Vtable;
                    instance->ReferenceCount = 1;
                    instance->ManagedHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
                    _instance = (nint)instance;
                    Interlocked.Increment(ref _activeInstances);
                }

                public void Dispose()
                {
                    if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    {
                        return;
                    }

                    nint instance = Interlocked.Exchange(ref _instance, 0);
                    if (instance != 0)
                    {
                        _ = ReleaseCore(instance);
                    }
                }

                internal int InvokeSynthetic()
                {
                    nint instance = Pointer;
                    if (instance == 0)
                    {
                        return EFail;
                    }

                    nint* vtable = *(nint**)instance;
                    return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)vtable[6])(instance, 0, 0);
                }

                internal void SuppressCallbacks()
                {
                    Volatile.Write(ref _suppressed, 1);
                }

                private static nint Vtable
                {
                    get
                    {
                        nint* vtable = (nint*)VtableStorage.Pointer;
                        return (nint)vtable;
                    }
                }

                private void Invoke()
                {
                    if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _suppressed) == 0)
                    {
                        _callback();
                    }
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static int QueryInterface(nint thisPointer, Guid* iid, nint* result)
                {
                    if (iid is null || result is null)
                    {
                        return EPointer;
                    }

                    *result = 0;
                    if (*iid != RawWinRtAbi.IUnknownIid
                        && *iid != RawWinRtAbi.IInspectableIid
                        && *iid != RawWinRtAbi.ColorValuesChangedHandlerIid)
                    {
                        return ENoInterface;
                    }

                    *result = thisPointer;
                    _ = AddRefCore(thisPointer);
                    return 0;
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static uint AddRef(nint thisPointer)
                {
                    return AddRefCore(thisPointer);
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static uint Release(nint thisPointer)
                {
                    return ReleaseCore(thisPointer);
                }

                private static uint AddRefCore(nint thisPointer)
                {
                    Instance* instance = (Instance*)thisPointer;
                    return (uint)Interlocked.Increment(ref instance->ReferenceCount);
                }

                private static uint ReleaseCore(nint thisPointer)
                {
                    Instance* instance = (Instance*)thisPointer;
                    int count = Interlocked.Decrement(ref instance->ReferenceCount);
                    if (count == 0)
                    {
                        GCHandle.FromIntPtr(instance->ManagedHandle).Free();
                        NativeMemory.Free(instance);
                        Interlocked.Decrement(ref _activeInstances);
                    }

                    return (uint)count;
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static int GetIids(nint thisPointer, uint* count, Guid** iids)
                {
                    if (count is null || iids is null)
                    {
                        return EPointer;
                    }

                    Guid* values = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
                    *values = RawWinRtAbi.ColorValuesChangedHandlerIid;
                    *count = 1;
                    *iids = values;
                    return 0;
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static int GetRuntimeClassName(nint thisPointer, nint* className)
                {
                    if (className is null)
                    {
                        return EPointer;
                    }

                    *className = 0;
                    return 0;
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static int GetTrustLevel(nint thisPointer, int* trustLevel)
                {
                    if (trustLevel is null)
                    {
                        return EPointer;
                    }

                    *trustLevel = 0;
                    return 0;
                }

                [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
                private static int Invoke(nint thisPointer, nint sender, nint eventArgs)
                {
                    try
                    {
                        Instance* instance = (Instance*)thisPointer;
                        RawColorValuesChangedHandler handler = (RawColorValuesChangedHandler)GCHandle.FromIntPtr(instance->ManagedHandle).Target!;
                        handler.Invoke();
                        return 0;
                    }
                    catch
                    {
                        return EFail;
                    }
                }

                private struct Instance
                {
                    internal nint Vtable;
                    internal int ReferenceCount;
                    internal nint ManagedHandle;
                }

                private static class VtableStorage
                {
                    internal static readonly nint Pointer = Create();

                    private static nint Create()
                    {
                        nint* vtable = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(RawColorValuesChangedHandler), 7 * sizeof(nint));
                        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
                        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
                        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
                        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, Guid**, int>)&GetIids;
                        vtable[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetRuntimeClassName;
                        vtable[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetTrustLevel;
                        vtable[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Invoke;
                        return (nint)vtable;
                    }
                }
            }
            """;
        return source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Write(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    private static void Verify(string generatedPath, string committedPath)
    {
        if (!File.Exists(committedPath))
        {
            throw new FileNotFoundException($"Committed generated file does not exist: {committedPath}", committedPath);
        }

        byte[] generated = File.ReadAllBytes(generatedPath);
        byte[] committed = File.ReadAllBytes(committedPath);
        if (!generated.AsSpan().SequenceEqual(committed))
        {
            throw new InvalidDataException(
                $"Generated file '{Path.GetFileName(generatedPath)}' differs: generated {Hash(generated)}, committed {Hash(committed)}.");
        }
    }

    private sealed record Projection(
        string DefaultInterfaceName,
        Guid DefaultInterfaceIid,
        string InterfaceName,
        Guid InterfaceIid,
        string[] Methods,
        string[] MethodSignatures,
        string EventHandlerSignature,
        Guid EventHandlerIid);

    private sealed class SignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => throw Unsupported();

        public string GetByReferenceType(string elementType) => $"{elementType}&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => throw Unsupported();

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(',', typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => throw Unsupported();

        public string GetGenericTypeParameter(object? genericContext, int index) => throw Unsupported();

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => throw Unsupported();

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.Void => "System.Void",
            _ => throw Unsupported(),
        };

        public string GetSZArrayType(string elementType) => throw Unsupported();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            GetFullName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            GetFullName(reader, reader.GetTypeReference(handle));

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static InvalidDataException Unsupported() => new("A selected WinMD signature uses an unsupported type shape.");
    }
}