from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_generation_tuning_is_removed_from_the_active_gui_and_backend_contract() -> None:
    active_files = (
        ROOT / "gui-winui3" / "Views" / "SettingsView.xaml",
        ROOT / "gui-winui3" / "Views" / "SettingsView.xaml.cs",
        ROOT / "gui-winui3" / "Models" / "Models.cs",
        ROOT / "backend-dotnet" / "RuBackend" / "Business" / "Settings" / "SettingsSnapshot.cs",
        ROOT / "backend-dotnet" / "RuBackend" / "Business" / "Settings" / "SettingsTransitionService.cs",
        ROOT / "backend-dotnet" / "RuBackend" / "Business" / "BusinessEndpoints.cs",
        ROOT / "backend-dotnet" / "RuBackend" / "Harness" / "Worker" / "LoopbackModelStepRunner.cs",
        ROOT / "backend-dotnet" / "RuAgent.Protocol" / "Harness" / "WorkerV2Contracts.cs",
    )
    removed_markers = (
        "TemperatureNumberBox",
        "TopKNumberBox",
        "MaxTokensNumberBox",
        "Temperature",
        "TopK",
        "MaxNewTokens",
        "max_tokens",
        "max_new_tokens",
        "temperature",
        "top_k",
    )

    for path in active_files:
        source = path.read_text(encoding="utf-8")
        for marker in removed_markers:
            assert marker not in source, f"{marker!r} remains in {path}"
