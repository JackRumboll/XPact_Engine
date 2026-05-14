# XPact Header Tool (XHT) — Manifest Schemas

XHT operates with **two distinct manifests** (per architectural decision #5). Subagents must keep them strictly separated.

## 1. `xht-input-manifest.json` — XBT → XHT (input)

**Direction**: XBT writes; XHT reads.
**Purpose**: tells XHT which modules and headers to scan.
**Output location**: `Engine/Cache/Intermediate/Build/<Target>/xht-input-manifest.json`

```json
{
  "schemaVersion": 1,
  "targetName": "XCoreEndToEnd",
  "targetType": "Program",
  "platform": "Win64",
  "configuration": "Development",
  "modules": [
    {
      "name": "XCore",
      "modulePath": "Engine/Source/Runtime/XCore",
      "outputDir": "Engine/Cache/Intermediate/Build/XCore/Generated",
      "publicHeaders": ["Public/Containers/Array.h", "..."],
      "privateHeaders": ["Private/Containers/UnrealString.cpp", "..."],
      "moduleVersion": "5.9.0"
    }
  ]
}
```

## 2. `bindings.json` — XHT → XPactBindingGen (output)

**Direction**: XHT writes per-module; Roslyn source generator reads.
**Purpose**: describes every XCLASS-marked native type with C-ABI signature for C# binding emission.
**Output location**: `Engine/Cache/Intermediate/Build/<Target>/<Module>/bindings.json`

```json
{
  "schemaVersion": 1,
  "moduleName": "XCore",
  "moduleAssemblyHash": "<sha256 of all scanned headers>",
  "types": [
    {
      "kind": "XCLASS",
      "name": "UTestActor",
      "fullyQualifiedName": "XPactEngine::UTestActor",
      "baseClass": "XObject",
      "classFlags": ["BlueprintType", "Blueprintable"],
      "castFlag": "CASTCLASS_None",
      "properties": [
        {
          "name": "DisplayName",
          "type": "FString",
          "marshallingAbi": {
            "managedType": "string",
            "nativeType": "byte*",
            "marshaller": "FStringMarshaller (UTF-8, null-terminated)"
          },
          "propertyFlags": ["EditAnywhere", "BlueprintReadWrite"]
        }
      ],
      "functions": [
        {
          "name": "Greet",
          "returnType": "void",
          "params": [
            { "name": "Name", "type": "FString", "marshallingAbi": {...} }
          ],
          "functionFlags": ["BlueprintCallable", "BlueprintNativeEvent"],
          "cAbiSymbol": "XPact_XCore_UTestActor_Greet"
        }
      ]
    },
    {
      "kind": "XSTRUCT",
      "name": "FVector",
      "fullyQualifiedName": "XPactEngine::FVector",
      "blittable": true,
      "fields": [
        { "name": "X", "type": "double", "offset": 0 },
        { "name": "Y", "type": "double", "offset": 8 },
        { "name": "Z", "type": "double", "offset": 16 }
      ],
      "size": 24,
      "alignment": 8
    },
    {
      "kind": "XENUM",
      "name": "EXTickGroup",
      "values": [
        { "name": "PrePhysics", "value": 0 },
        { "name": "DuringPhysics", "value": 1 }
      ]
    }
  ]
}
```

## Marshalling ABI Discipline (decision #19)

| C++ type | Native ABI | Managed type | Marshaller |
|---|---|---|---|
| `FString` | `byte*` (UTF-8, null-terminated for short, length-prefixed for long) | `string` | `FStringMarshaller` |
| `FName` | `FNameHandle` (struct holding hash + comparison index) | `FName` (managed wrapper) | `FNameMarshaller` |
| `FVector` | `(double, double, double)` 24-byte blittable | `FVector` blittable struct | none — direct |
| `FQuat` | `(double, double, double, double)` 32-byte blittable | `FQuat` blittable | none |
| `FTransform` | `(FQuat, FVector, FVector)` 80-byte blittable | `FTransform` blittable | none |
| `XObject*` | `IntPtr` | wrapper class holding NativeHandle | `XObjectMarshaller` |
| `bool` | `byte` | `bool` (managed bool→byte conversion at boundary) | runtime |
| `char` | `byte` | `byte` | direct |
| `TArray<T>` | C-ABI varies by T; ranged-pointer-pair `(T* begin, int32 count)` for blittable T | `XArray<T>` wrapper | custom per-element |

## XBT → C# Binding-Generator Injection

Per decision #53, XBT emits per-target:
- `Engine/Source/Programs/Targets/<Target>/xpact.modules.props` — lists module names → bindings.json paths.
- `Engine/Source/Programs/Targets/<Target>/Directory.Build.targets` — hoists `bindings.json` paths into `<AdditionalFiles>` for Roslyn SG consumption.

C# game-script projects inherit these via standard MSBuild `Directory.Build.props/.targets` discovery walking up the directory tree.

## UhtInputCache

XHT must implement the input cache (port from `EpicGames.UHT/Utils/UhtInputCache.cs`) per Round-2 R2-I7. Cache key = header content hash + module configuration; value = parsed AST + emitted `.generated.h` / `bindings.json`. Re-running XHT on unchanged inputs reads cache in <10% of cold time.

## Schema Versioning

`schemaVersion: 1` in every manifest. Bump on incompatible changes. Subagents reject manifests with unrecognized `schemaVersion` rather than silently misinterpreting.
