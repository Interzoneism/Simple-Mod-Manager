using System.Windows;

namespace VintageStoryModManager.Helpers
{
    /// <summary>
    /// A helper class that enables bindings in non-visual tree elements (like DataGridColumn)
    /// by inheriting from Freezable. This allows it to participate in the data binding
    /// infrastructure and access the DataContext.
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore()
        {
            return new BindingProxy();
        }

        public object Data
        {
            get { return GetValue(DataProperty); }
            set { SetValue(DataProperty, value); }
        }

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(
                nameof(Data),
                typeof(object),
                typeof(BindingProxy),
                new PropertyMetadata(null));
    }
}
