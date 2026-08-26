using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterDesktop.Shell.Dock;

internal sealed class FolderInputWindow : Window
{
	private readonly TextBox _textBox;

	public string FolderName { get; private set; } = string.Empty;

	public FolderInputWindow()
	{
		base.Title = "新建分类";
		base.Width = 320.0;
		base.Height = 160.0;
		base.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		base.ResizeMode = ResizeMode.NoResize;
		base.WindowStyle = WindowStyle.None;
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		base.ShowInTaskbar = false;
		Grid grid = new Grid
		{
			RowDefinitions = 
			{
				new RowDefinition
				{
					Height = GridLength.Auto
				},
				new RowDefinition
				{
					Height = GridLength.Auto
				},
				new RowDefinition
				{
					Height = GridLength.Auto
				}
			}
		};
		TextBlock element = new TextBlock
		{
			Text = "新建分类",
			Foreground = Brushes.White,
			FontSize = 16.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
		Grid.SetRow(element, 0);
		_textBox = new TextBox
		{
			Height = 30.0,
			FontSize = 14.0,
			Foreground = Brushes.White,
			Background = new SolidColorBrush(Color.FromRgb(18, 18, 22)),
			CaretBrush = Brushes.White,
			BorderBrush = new SolidColorBrush(Color.FromArgb(128, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(8.0, 0.0, 8.0, 0.0)
		};
		_textBox.KeyDown += OnTextBoxKeyDown;
		Grid.SetRow(_textBox, 1);
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
		};
		Button button = new Button
		{
			Content = "取消",
			Width = 80.0,
			Height = 30.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			Foreground = Brushes.White,
			Background = new SolidColorBrush(Color.FromRgb(68, 68, 76)),
			BorderThickness = new Thickness(0.0)
		};
		button.Click += delegate
		{
			base.DialogResult = false;
		};
		stackPanel.Children.Add(button);
		Button button2 = new Button
		{
			Content = "确定",
			Width = 80.0,
			Height = 30.0,
			Foreground = Brushes.White,
			Background = new SolidColorBrush(Color.FromRgb(43, 138, 62)),
			BorderThickness = new Thickness(0.0)
		};
		button2.Click += delegate
		{
			Confirm();
		};
		stackPanel.Children.Add(button2);
		Grid.SetRow(stackPanel, 2);
		grid.Children.Add(element);
		grid.Children.Add(_textBox);
		grid.Children.Add(stackPanel);
		Border border = new Border
		{
			CornerRadius = new CornerRadius(12.0),
			Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(120, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(16.0)
		};
		border.Child = grid;
		base.Content = border;
		base.Loaded += delegate
		{
			_textBox.Focus();
			_textBox.SelectAll();
		};
	}

	private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Invalid comparison between Unknown and I4
		if ((int)e.Key == 6)
		{
			Confirm();
		}
		else if ((int)e.Key == 13)
		{
			base.DialogResult = false;
		}
	}

	private void Confirm()
	{
		string text = _textBox.Text?.Trim() ?? string.Empty;
		if (string.IsNullOrWhiteSpace(text))
		{
			_textBox.Focus();
			return;
		}
		FolderName = text;
		base.DialogResult = true;
	}
}
