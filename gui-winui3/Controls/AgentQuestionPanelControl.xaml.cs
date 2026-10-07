using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>The answer the user submitted for a pending question.</summary>
    public sealed class AgentQuestionAnsweredEventArgs : EventArgs
    {
        public AgentQuestionAnsweredEventArgs(string answer)
        {
            Answer = answer ?? string.Empty;
        }

        public string Answer { get; }
    }

    /// <summary>
    /// The inline question prompt: the question the agent is waiting on, its
    /// optional canned answers, and a free-text answer box.
    ///
    /// Extracted from ChatView so the prompt owns its own presentation and only
    /// reports the answer back. The view still decides when the prompt is shown
    /// and what it says; the controller owns the submit route.
    /// </summary>
    public sealed partial class AgentQuestionPanelControl : UserControl
    {
        /// <summary>Raised when the user submits an answer.</summary>
        public event EventHandler<AgentQuestionAnsweredEventArgs>? AnswerRequested;

        private IReadOnlyList<string> _options = Array.Empty<string>();
        private string? _inputId;

        public AgentQuestionPanelControl()
        {
            InitializeComponent();
            AnswerTextBox.MaxLength = AgentQuestionPresentation.MaximumAnswerLength;
        }

        public bool IsShown => Panel.Visibility == Visibility.Visible;

        public bool IsSubmitEnabled => SubmitButton.IsEnabled;

        /// <summary>Question currently presented, for tests and diagnostics.</summary>
        public string Question => QuestionText.Text;

        /// <summary>Option labels currently presented, empty when the question is free-form.</summary>
        public IReadOnlyList<string> Options => _options;

        public bool IsOptionsShown => OptionsList.Visibility == Visibility.Visible;

        /// <summary>
        /// Presents one pending question. An empty option list hides the option
        /// row, so a free-form question does not show an empty button strip.
        /// A repeated projection of the same question leaves the typed answer
        /// alone; a different question clears it, and <see cref="Hide"/> clears
        /// it in every case.
        /// </summary>
        public void Show(string inputId, string question, IReadOnlyList<string>? options = null)
        {
            if (!string.Equals(_inputId, inputId, StringComparison.Ordinal))
            {
                _inputId = inputId;
                AnswerTextBox.Text = string.Empty;
            }

            QuestionText.Text = question ?? string.Empty;
            _options = options ?? Array.Empty<string>();
            if (_options.Count > 0)
            {
                OptionsList.ItemsSource = _options;
                OptionsList.Visibility = Visibility.Visible;
            }
            else
            {
                OptionsList.ItemsSource = null;
                OptionsList.Visibility = Visibility.Collapsed;
            }

            Panel.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Panel.Visibility = Visibility.Collapsed;
            QuestionText.Text = string.Empty;
            AnswerTextBox.Text = string.Empty;
            OptionsList.ItemsSource = null;
            OptionsList.Visibility = Visibility.Collapsed;
            _options = Array.Empty<string>();
            _inputId = null;
        }

        /// <summary>
        /// Enables or disables answering while a request is in flight. The controls
        /// stay visible: hiding them mid-request would read as the prompt vanishing.
        /// </summary>
        public void SetSubmitEnabled(bool enabled)
        {
            SubmitButton.IsEnabled = enabled;
            AnswerTextBox.IsEnabled = enabled;
            OptionsList.IsEnabled = enabled;
        }

        /// <summary>Moves focus onto the answer box, which is where a question is answered.</summary>
        public void FocusAnswer() => AnswerTextBox?.Focus(FocusState.Programmatic);

        /// <summary>
        /// Escape from any part of the panel is swallowed and does nothing: there is
        /// no decision to deny, and letting it bubble would stop the parked run.
        /// </summary>
        private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
            }
        }

        private void OnAnswerKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            SubmitAnswer();
        }

        private void OnSubmitClick(object sender, RoutedEventArgs e) => SubmitAnswer();

        private void OnOptionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: string option })
            {
                Submit(option);
            }
        }

        private void SubmitAnswer() => Submit(AnswerTextBox.Text);

        private void Submit(string? answer)
        {
            string trimmed = answer?.Trim() ?? string.Empty;
            if (!IsSubmitEnabled || !AgentQuestionPresentation.IsAnswerWithinBounds(trimmed))
            {
                return;
            }

            AnswerRequested?.Invoke(this, new AgentQuestionAnsweredEventArgs(trimmed));
        }
    }
}
