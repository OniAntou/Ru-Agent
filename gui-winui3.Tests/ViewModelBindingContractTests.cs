using System.Reflection;
using System.Text.RegularExpressions;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Every <c>{x:Bind ViewModel.Member}</c> in a view's markup must name a public member of
/// that view's model. The XAML compiler checks this too, but only in a full WinUI build;
/// this keeps the contract visible to the test suite that guards the view-model seam.
/// </summary>
public sealed partial class ViewModelBindingContractTests
{
    [Theory]
    [InlineData("SettingsView.xaml", typeof(SettingsViewModel))]
    public void View_bindings_name_public_view_model_members(string view, Type viewModel)
    {
        string markup = GuiSource.Text("Views", view);
        var members = ViewModelPath().Matches(markup).Select(match => match.Groups[1].Value).Distinct().ToArray();

        Assert.NotEmpty(members);
        foreach (string member in members)
        {
            Assert.True(
                viewModel.GetMember(member, BindingFlags.Public | BindingFlags.Instance).Length > 0,
                $"{view} binds ViewModel.{member}, which {viewModel.Name} does not expose.");
        }
    }

    [GeneratedRegex(@"\{x:Bind\s+(?:\w+\()?ViewModel\.(\w+)")]
    private static partial Regex ViewModelPath();
}
