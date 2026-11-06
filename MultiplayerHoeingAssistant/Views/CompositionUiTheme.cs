using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>编排界面的统一紫金控件。模板包含禁用、焦点、下拉项和滚动条，避免系统白色 chrome。</summary>
public static class CompositionUiTheme
{
    public static void Apply(FrameworkElement scope)
    {
        if (scope.Resources.Contains("CompositionButton")) return;
        scope.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Resources));
    }

    public static void StyleButton(Button button, bool primary = false)
        => button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "CompositionPrimaryButton" : "CompositionButton");
    public static void StyleCombo(ComboBox combo)
    {
        combo.SetResourceReference(FrameworkElement.StyleProperty, "CompositionComboBox");
        combo.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "CompositionComboItem");
    }
    public static void StyleCheck(CheckBox check)
        => check.SetResourceReference(FrameworkElement.StyleProperty, "CompositionCheckBox");
    public static void StyleScroll(ScrollViewer viewer)
        => viewer.SetResourceReference(FrameworkElement.StyleProperty, "CompositionScrollViewer");

    private const string Resources = """
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style x:Key="CompositionExpander" TargetType="Expander">
        <Setter Property="Foreground" Value="#F4F0F8"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Expander">
          <StackPanel>
            <ToggleButton Foreground="{TemplateBinding Foreground}" IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                          HorizontalContentAlignment="Stretch" FocusVisualStyle="{x:Null}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                <Border x:Name="HeaderBorder" Padding="3,4" BorderThickness="1" BorderBrush="Transparent" CornerRadius="4">
                  <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Path x:Name="Arrow" Data="M 2,1 L 7,6 L 2,11" Stroke="#E8C96D" StrokeThickness="1.5" HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/>
                    <ContentPresenter Grid.Column="1" VerticalAlignment="Center" TextElement.Foreground="{TemplateBinding Foreground}"/>
                  </Grid>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter></Trigger>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="HeaderBorder" Property="Background" Value="#383148"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="HeaderBorder" Property="BorderBrush" Value="#E8C96D"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></ToggleButton.Template>
              <ContentPresenter ContentSource="Header"/>
            </ToggleButton>
            <ContentPresenter x:Name="Body" ContentSource="Content"/>
          </StackPanel>
          <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="False"><Setter TargetName="Body" Property="Visibility" Value="Collapsed"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionButton" TargetType="Button">
        <Setter Property="Foreground" Value="#F4F0F8"/><Setter Property="Background" Value="#3B3351"/>
        <Setter Property="BorderBrush" Value="#796A88"/><Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="10,5"/><Setter Property="MinHeight" Value="30"/><Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" RecognizesAccessKey="True"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#E8C96D"/><Setter TargetName="Chrome" Property="Opacity" Value="0.9"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#F1D787"/><Setter TargetName="Chrome" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Opacity" Value="0.72"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Background" Value="#29253A"/><Setter TargetName="Chrome" Property="BorderBrush" Value="#51495E"/><Setter Property="Foreground" Value="#8D849F"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionPrimaryButton" TargetType="Button" BasedOn="{StaticResource CompositionButton}">
        <Setter Property="Background" Value="#E8C96D"/><Setter Property="Foreground" Value="#241D35"/><Setter Property="BorderBrush" Value="#E8C96D"/><Setter Property="FontWeight" Value="SemiBold"/>
      </Style>
      <Style x:Key="CompositionComboItem" TargetType="ComboBoxItem">
        <Setter Property="Foreground" Value="#F1EBF7"/><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="10,7"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="Item" Background="{TemplateBinding Background}" BorderBrush="Transparent" BorderThickness="1" Padding="{TemplateBinding Padding}" CornerRadius="4"><ContentPresenter/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#4B4059"/><Setter TargetName="Item" Property="BorderBrush" Value="#B79C60"/></Trigger>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Item" Property="Background" Value="#51432E"/><Setter Property="Foreground" Value="#F4DB99"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#8D849F"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionComboBox" TargetType="ComboBox">
        <Setter Property="Foreground" Value="#F1EBF7"/><Setter Property="Background" Value="#211D35"/><Setter Property="BorderBrush" Value="#A88F55"/><Setter Property="BorderThickness" Value="1"/>
        <Setter Property="MinHeight" Value="32"/><Setter Property="Padding" Value="9,5"/><Setter Property="ScrollViewer.CanContentScroll" Value="True"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="ItemContainerStyle" Value="{StaticResource CompositionComboItem}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                <Border x:Name="Box" Background="{Binding Background, RelativeSource={RelativeSource AncestorType=ComboBox}}" BorderBrush="{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}" BorderThickness="1" CornerRadius="5">
                  <Grid Margin="9,5"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="16"/></Grid.ColumnDefinitions>
                    <ContentPresenter Content="{Binding SelectionBoxItem, RelativeSource={RelativeSource AncestorType=ComboBox}}" ContentTemplate="{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource AncestorType=ComboBox}}" TextElement.Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=ComboBox}}" VerticalAlignment="Center"/>
                    <Path Grid.Column="1" Data="M0,0 L4,4 L8,0" Stroke="#E8C96D" StrokeThickness="1.6" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                  </Grid>
                </Border>
                <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#E8C96D"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="None">
              <Border Background="#28223E" BorderBrush="#A88F55" BorderThickness="1" CornerRadius="5" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer Style="{DynamicResource CompositionScrollViewer}" CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="#F1D787"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#8D849F"/><Setter Property="Background" Value="#29253A"/><Setter Property="BorderBrush" Value="#51495E"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionCheckBox" TargetType="CheckBox">
        <Setter Property="Foreground" Value="#F1EBF7"/><Setter Property="Cursor" Value="Hand"/><Setter Property="MinHeight" Value="25"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
            <Border x:Name="Box" Width="16" Height="16" Background="#211D35" BorderBrush="#93829E" BorderThickness="1" CornerRadius="3">
              <Path x:Name="Tick" Data="M2,7 L6,11 L13,3" Stroke="#E8C96D" StrokeThickness="2" Visibility="Collapsed"/>
            </Border>
            <ContentPresenter Margin="7,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Tick" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="BorderBrush" Value="#D5B970"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#F1D787"/><Setter TargetName="Box" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionScrollThumb" TargetType="Thumb">
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Border Background="#776586" CornerRadius="4" Margin="2,0"/></ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionScrollPage" TargetType="RepeatButton">
        <Setter Property="Focusable" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionScrollBar" TargetType="ScrollBar">
        <Setter Property="Width" Value="10"/><Setter Property="Background" Value="#211D35"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
          <Border Background="{TemplateBinding Background}" CornerRadius="4"><Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource CompositionScrollPage}" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Style="{StaticResource CompositionScrollThumb}"/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource CompositionScrollPage}" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton>
          </Track></Border>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="CompositionScrollViewer" TargetType="ScrollViewer">
        <Setter Property="HorizontalScrollBarVisibility" Value="Disabled"/><Setter Property="VerticalScrollBarVisibility" Value="Auto"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollViewer">
          <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" CanContentScroll="{TemplateBinding CanContentScroll}" Margin="{TemplateBinding Padding}"/>
            <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Style="{StaticResource CompositionScrollBar}" Orientation="Vertical" Minimum="0" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}" Value="{TemplateBinding VerticalOffset}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
          </Grid>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
    </ResourceDictionary>
    """;
}
