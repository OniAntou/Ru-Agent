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
    [InlineData("SettingsView.xaml", "ViewModel", typeof(SettingsViewModel))]
    [InlineData("ChatView.xaml", "ModelSelection", typeof(ChatModelPickerViewModel))]
    public void View_bindings_name_public_view_model_members(string view, string property, Type viewModel)
    {
        string markup = GuiSource.Text("Views", view);
        var path = new Regex(@"\{x:Bind\s+(?:\w+\()?" + property + @"\.(\w+)");
        var members = path.Matches(markup).Select(match => match.Groups[1].Value).Distinct().ToArray();

        Assert.NotEmpty(members);
        foreach (string member in members)
        {
            Assert.True(
                viewModel.GetMember(member, BindingFlags.Public | BindingFlags.Instance).Length > 0,
                $"{view} binds {property}.{member}, which {viewModel.Name} does not expose.");
        }
    }
}
