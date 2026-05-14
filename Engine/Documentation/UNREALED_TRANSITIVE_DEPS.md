# UnrealEd Transitive Dep Manifest — XPact Engine

Per architectural decisions #25, #29, #49, this manifest enumerates every PublicDependencyModuleName + PrivateDependencyModuleName + DynamicallyLoadedModuleName of `Engine/Source/Editor/UnrealEd/UnrealEd.Build.cs` with XPact's disposition.

**Status: TEMPLATE — full content authored by manager pre-Phase-4a-deps (1–2 weeks read-only triage).**

## Schema

Each row:

| Column | Allowed values | Meaning |
|---|---|---|
| Module Name | string | as in UnrealEd.Build.cs |
| Location | path | repo-relative to module's source root |
| Dep Type | Public / Private / DynamicallyLoaded / IncludePathOnly | from UnrealEd.Build.cs |
| XPact Disposition | PORT / STUB / DEFER / STRIP-DEP / ALREADY-PORTED | action |
| Target Phase | "4a-deps", "4a-shell", "4a-property", "4a-blueprint", "4b-prereqs", "4b", "4c", "5", "6", "7+" | when |
| Rationale | one-line | why |

## Disposition Definitions

- **PORT** — full faithful port; subagent task brief produced.
- **STUB** — compile-only stub satisfying linker; runtime no-op.
- **DEFER** — STUB now, real port Phase 6/7+.
- **STRIP-DEP** — modify UnrealEd to remove this dep (used for Verse/Fortnite/Chaos hooks).
- **ALREADY-PORTED** — module is in a prior phase already.

## Decided-now Rows (from architectural decisions)

| Module | Location | Dep Type | Disposition | Phase | Rationale |
|---|---|---|---|---|---|
| InterchangeCore | `Runtime/Interchange/Core/` | Public | PORT | 4a-deps | Decision #38 |
| InterchangeEngine | `Runtime/Interchange/Engine/` | Public | PORT | 4a-deps | Decision #38; depends on Phase 3 Engine |
| MaterialShaderQualitySettings | `Runtime/MaterialShaderQualitySettings/` | Public | PORT | 4a-deps | UnrealEd CircRef |
| GameplayTasks | `Runtime/GameplayTasks/` | Public | PORT | 4a-deps | — |
| CommonMenuExtensions | `Editor/CommonMenuExtensions/` | Public | PORT | 4a-shell | Also Persona dep |
| NetworkFileSystem | `Runtime/NetworkFileSystem/` | Public | STUB | 4a-deps | Full Phase 7+ |
| EngineSettings | `Runtime/EngineSettings/` | Public + Private | PORT | 4a-deps | — |
| Documentation | `Editor/Documentation/` | IncludePath | ALREADY-PORTED | 4a-shell | Decision in 4a-shell |
| TargetPlatform | `Developer/TargetPlatform/` | Public | PORT | 4a-deps | Also Phase 5 cook |
| EditorSubsystem | `Editor/EditorSubsystem/` | Public | ALREADY-PORTED | 4a-shell | UEditorSubsystem |
| WidgetRegistration | `Developer/WidgetRegistration/` | Public | ALREADY-PORTED | 4a-shell | Decision #29 |
| UnrealEdMessages | `Editor/UnrealEdMessages/` | Public | ALREADY-PORTED | 4a-shell | Decision #29 |
| AssetTagsEditor | `Editor/AssetTagsEditor/` | Private | PORT | 4a-shell | — |
| BSPUtils | `Developer/BSPUtils/` | Private | PORT | 4a-deps | — |
| CinematicCamera | `Runtime/CinematicCamera/` | Private | PORT | 4a-deps | — |
| CookMetadata | `Developer/CookMetadata/` | Private | PORT | 4a-deps | — |
| DataLayerEditor (stub) | `Editor/DataLayerEditor/` | Private | STUB | 4a-shell | Decision #37, #69 |
| DataLayerEditor (full) | same | — | DEFER | 7+ | Decision #69 |
| Zen client lib | `Developer/Zen/` | Private | PORT | 4a-deps | Zen client only; Zen Server deferred 7+ |
| IESFile | `Runtime/IESFile/` | Private | PORT | 4a-deps | Lighting |
| JsonObjectGraph | `Runtime/Experimental/JsonObjectGraph/` | Private | PORT | 4a-deps | — |
| LauncherServices | `Developer/LauncherServices/` | Private | PORT | 4a-deps | — |
| LauncherPlatform | `Runtime/Portal/LauncherPlatform/` | Private | PORT | 4a-deps | — |
| Landscape (runtime stub) | `Runtime/Landscape/` | Private | STUB | 4a-deps | Full Phase 7+ |
| MeshPaint | `Editor/MeshPaint/` | CircRef | STUB | 4a-shell | Full Phase 7+ |
| Foliage (runtime stub) | `Runtime/Foliage/` | Private | STUB | 4a-deps | Full Phase 7+ |
| FoliageEdit | `Editor/FoliageEdit/` | Private | STUB | 4a-shell | Full Phase 7+ |
| AddContentDialog | `Editor/AddContentDialog/` | Private | PORT | 4a-shell | Decision #45 |
| GameProjectGeneration | `Editor/GameProjectGeneration/` | Private | PORT | 4a-shell | Decision #45 — project picker |
| NewLevelDialog | `Editor/NewLevelDialog/` | Private | PORT | 4a-shell | Decision #45 |
| HierarchicalLODUtilities | `Developer/HierarchicalLODUtilities/` | Private | PORT | 4a-deps | — |
| Analytics | `Runtime/Analytics/Analytics/` | Private | STUB | 4a-deps | Telemetry deferred |
| AnalyticsET | `Runtime/Analytics/AnalyticsET/` | Private | STUB | 4a-deps | — |
| PixelInspectorModule | `Editor/PixelInspector/` | Private | STUB | 4a-shell | Optional |
| ViewportInteraction | `Editor/ViewportInteraction/` | CircRef | STUB | 4a-shell | VR-only |
| VREditor | `Editor/VREditor/` | CircRef | STUB | 4a-shell | VR-only; full Phase 7+ |
| ClothingSystemEditor | `Editor/ClothingSystemEditor/` | — | ALREADY-PORTED | 4b-prereqs | Persona dep |
| ClothingSystemEditorInterface | `Editor/ClothingSystemEditorInterface/` | — | ALREADY-PORTED | 4b-prereqs | — |
| ClothingSystemRuntimeCommon | `Runtime/ClothingSystemRuntime/` | — | ALREADY-PORTED | 4b-prereqs | — |
| ClothingSystemRuntimeInterface | `Runtime/` | — | ALREADY-PORTED | 4b-prereqs | — |
| ClothingSystemRuntimeNv | `Runtime/` | — | ALREADY-PORTED | 4b-prereqs | — |
| PakFileUtilities | `Developer/PakFileUtilities/` | Private | PORT | 4a-deps | Phase 5 cook also uses |
| FreeImage | `ThirdParty/FreeImage/` | Private | PORT (vendored) | 4a-deps | — |
| UATHelper | `Editor/UATHelper/` | Private | PORT | 4a-shell | Phase 5 cook driver |
| IoStoreUtilities | `Developer/IoStoreUtilities/` | Private | ALREADY-PORTED | 5 | Phase 5 cook |
| TraceAnalysis | `Developer/TraceAnalysis/` | Private | PORT | 4a-deps | TraceInsights base |
| TraceServices | `Developer/TraceServices/` | Private | PORT | 4a-deps | — |
| BuildSettings | `Runtime/BuildSettings/` | Private | PORT | 4a-deps | — |
| VirtualizationEditor | `Editor/VirtualizationEditor/` | Private | PORT | 4a-shell | — |
| GeometryCore | `Runtime/GeometryCore/` | Private | ALREADY-PORTED | 2 | — |
| Renderer | `Runtime/Renderer/` | Private | ALREADY-PORTED | 2 | MobileShadingRenderer subset |
| WindowsPlatformFeatures | `Runtime/Windows/WindowsPlatformFeatures/` | Private (Win64) | PORT | 4a-shell | Win64 only |
| GameplayMediaEncoder | `Runtime/Windows/GameplayMediaEncoder/` | Private (Win64) | STUB | 4a-shell | Encoder dep |
| ASDCore | `Runtime/Windows/ASDCore/` | Private (Win64) | STUB | 4a-shell | Audio Spatial Decoder; defer |
| NavigationSystem (subset) | `Runtime/NavigationSystem/` | Public | PORT (subset) | 4a-deps | Full Phase 5 |
| MeshDescription | `Runtime/MeshDescription/` | Public | PORT | 4a-deps | Phase 5 full continues |
| StaticMeshDescription | `Runtime/StaticMeshDescription/` | Public | PORT | 4a-deps | — |
| SkeletalMeshDescription | `Runtime/SkeletalMeshDescription/` | Public | PORT | 4a-deps | — |
| MeshBuilder (subset) | `Developer/MeshBuilder/` | Public | PORT | 4a-deps | Full Phase 5 |
| PhysicsUtilities | `Runtime/Engine/Classes/PhysicsEngine/` | Public | PORT | 4a-deps | Engine subset |
| HTTP | `Runtime/HTTP/` | Public | PORT | 4a-deps | — |
| Networking | `Runtime/Networking/` | Public | PORT | 4a-deps | Decision #66; transport |
| Sockets | `Runtime/Sockets/` | Public | PORT | 4a-deps | — |
| SourceControl | `Developer/SourceControl/` | Public | PORT | 4a-deps | Subset; full providers Phase 6 |
| UMG | `Runtime/UMG/` | Public | ALREADY-PORTED | 4b | UMG runtime |
| Localization (runtime) | `Runtime/Core/Public/Internationalization/` | — | ALREADY-PORTED | 1 | FText etc. in Phase 1 |
| AudioEditor (subset) | `Editor/AudioEditor/` | Public | PORT (subset) | 4a-deps | Full Phase 4c |
| AdvancedPreviewScene | `Editor/AdvancedPreviewScene/` | — | PORT | 4a-deps | Decision #58 |
| StructUtils (Plugin) | `Plugins/Experimental/StructUtils/` | — | PORT | 4a-deps | Decision #56 |
| EditorConfig | `Editor/EditorConfig/` | — | PORT | 4a-deps | Decision #66 |
| FunctionalTesting | `Developer/FunctionalTesting/` | — | PORT | 4a-deps | Phase 5 acceptance tests |
| InstallBundleManager | `Runtime/InstallBundleManager/` | — | PORT | 4a-deps | Launch dep |
| MessagingCommon | `Runtime/MessagingCommon/` | — | PORT | 4a-deps | UnrealEd + Phase 6 Concert |
| AutomationWorker | `Runtime/AutomationWorker/` | — | PORT | 4a-deps | Launch dep |
| VirtualTexturingEditor | `Editor/VirtualTexturingEditor/` | — | STUB | 4a-deps | — |
| MRMesh | `Runtime/MRMesh/` | — | STUB | 4a-deps | XR-related; full Phase 6 |
| Net (types-only) | `Runtime/Net/` | — | ALREADY-PORTED | 3 | Types-only stub |

## To-Be-Triaged DynamicallyLoaded Rows

UnrealEd's `DynamicallyLoadedModuleNames` list — these modules are loaded by UnrealEd at runtime when assets are opened. Manager triages each pre-Phase-4a-deps:

- ContentBrowser ✓ (already 4a-shell), DetailCustomizations ✓ (4a-property), MainFrame ✓ (4a-shell), KismetCompiler ✓ (4a-blueprint), MeshUtilities ✓ (5)
- TextureEditor ✓ (4a-shell), StringTableEditor ✓ (4a-shell), Persona ✓ (4b), PhysicsAssetEditor ✓ (4b), Blutility ✓ (4a-shell), PlacementMode ✓ (4a-property), SettingsEditor ✓ (4a-shell), EditorSettingsViewer ✓ (4a-shell), ProjectSettingsViewer ✓ (4a-shell), BehaviorTreeEditor ✓ (4c), CurveTableEditor ✓ (4a-shell), DataTableEditor ✓ (4a-shell), FontEditor ✓ (4a-shell), ClassViewer ✓ (4a-property), StructViewer ✓ (4a-property), CollectionManager ✓ (4a-property), ComponentVisualizers ✓ (4a-shell), MergeActors ✓ (4a-shell), TurnkeySupport (stub 4a-shell), PackagesDialog (4a-shell), ScriptableEditorWidgets (4a-shell), WorkspaceMenuStructure ✓ (4a-shell), RenderResourceViewer ✓ (4a-shell), PortalProxies (stub), PortalServices (stub), OverlayEditor (stub), ClothPainter (stub), Media (stub), VirtualTexturingEditor (stub), WorldBookmark ✓ (4a-shell), WorldPartitionEditor (defer 7+; stub 4a-shell), CSVtoSVG (?), SourceControlWindowExtender ✓ (Phase 6), AnimationSettings ✓ (4b), GameplayDebuggerEditor ✓ (4a-shell), StructUtilsTestSuite (optional)
- About 14 more — manager pre-Phase-4a-deps will complete this enumeration.

## Manager Action Items

- 1–2 weeks pre-Phase-4a-deps walking `Engine/Source/Editor/UnrealEd/UnrealEd.Build.cs` line by line.
- Each module gets a row above.
- Subagents reading this file as part of Phase 4a-deps brief must NOT modify rows; only the manager updates.
