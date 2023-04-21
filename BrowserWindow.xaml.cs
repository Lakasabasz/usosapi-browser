using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;

namespace UsosApiBrowser
{
    public partial class BrowserWindow : Window
    {
        /// <summary>
        /// This holds cached information on the data user is filling into various
        /// inputs. Makes it a bit easier for a user to not get annoyed with this app.
        /// </summary>
        public VarsCache varsCache = new();

        /// <summary>
        /// Used to connect to USOS API installations.
        /// </summary>
        private ApiConnector apiConnector;

        public BrowserWindow()
        {
            InitializeComponent();
            MessageBox.Show("Please note, that this is a development tool only and it might be a bit buggy. " +
                "However, this stands for this client application only, not the USOS API itself!",
                "USOS API Browser", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

            varsCache.BindWithTextBox("consumer_key", consumerKeyTextbox);
            varsCache.BindWithTextBox("consumer_secret", consumerSecretTextbox);
            varsCache.BindWithTextBox("token", tokenTextbox);
            varsCache.BindWithTextBox("token_secret", tokenSecretTextbox);

            /* We use a "mother installation" for the first USOS API request. We need to
             * get a list of all USOS API installations. */

            var motherInstallation = new ApiInstallation
            {
                base_url = "http://apps.usos.edu.pl/" // will change when out of Beta!
            };
            apiConnector = new ApiConnector(motherInstallation);
            apiConnector.BeginRequest += apiConnector_BeginRequest;
            apiConnector.EndRequest += apiConnector_EndRequest;

            /* Fill up the installations list. */

            try
            {
                installationsComboBox.Items.Clear();
                var installations = apiConnector.GetInstallations();
                installations.Add(new ApiInstallation { base_url = "https://usosapi.ath.bielsko.pl/" });
                foreach (var installation in installations)
                {
                    installationsComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = installation.base_url,
                        Tag = installation
                    });
                }

                installationsComboBox.SelectedItem = installationsComboBox.Items[^1];
            }
            catch (WebException)
            {
                MessageBox.Show("Error occured when trying to access USOS API mother server. Could not populate USOS API installations list.",
                    "Network error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        }

        void apiConnector_BeginRequest(object sender, EventArgs e)
        {
            /* Change a cursor to Wait when API request begins... */
            Cursor = Cursors.Wait;
        }

        void apiConnector_EndRequest(object sender, EventArgs e)
        {
            /* Change a cursor back to Arrow when API request ends. */
            Cursor = Cursors.Arrow;
        }

        /// <summary>
        /// List of valid scopes (as retrieved from a currently selected API installation).
        /// </summary>
        public List<ApiScope> scopes;

        /// <summary>
        /// Refresh the list of valid scopes.
        /// </summary>
        private void RefreshScopes()
        {
            scopes = apiConnector.GetScopes();
        }

        /// <summary>
        /// Refresh the methods tree.
        /// </summary>
        /// <returns>False if could not connect to the current API installation.</returns>
        private bool RefreshTree()
        {
            /* Retrieving a list of all API methods. */

            List<ApiMethod> methods;
            try
            {
                methods = apiConnector.GetMethods();
            }
            catch (WebException ex)
            {
                MessageBox.Show("Could not connect to selected installation.\n" + ex.Message + "\n"
                    + ApiConnector.ReadResponse(ex.Response), "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not connect to selected installation.\n" + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            /* Building a tree of modules and methods. This is done by analyzing method names. */

            foreach (ApiMethod method in methods)
            {
                string[] path = method.name.Split('/');
                TreeViewItem currentnode = null;
                for (var i = 1; i < path.Length; i++)
                {
                    string part = path[i];
                    bool already_present = false;
                    ItemCollection items = (i == 1) ? methodsTreeView.Items : currentnode.Items;
                    foreach (TreeViewItem item in items)
                    {
                        if ((string)item.Header == part) {
                            already_present = true;
                            currentnode = item;
                        }
                    }
                    if (!already_present)
                    {
                        currentnode = new TreeViewItem { Header = part };
                        if (i == path.Length - 1)
                        {
                            currentnode.Tag = method;
                            currentnode.ToolTip = method.brief_description;
                        }
                        items.Add(currentnode);
                    }
                }
            }

            /* Expand all nodes of the tree (if the are more than 50 methods in this installation,
             * then we skip this step), and select an initial method (request_token). */

            foreach (TreeViewItem item in methodsTreeView.Items)
            {
                if (methods.Count < 50)
                    item.ExpandSubtree();
                if ((string)item.Header == "oauth")
                {
                    item.ExpandSubtree();
                    foreach (TreeViewItem subitem in item.Items)
                        if ((string)subitem.Header == "request_token")
                            subitem.IsSelected = true;
                }
            }
            return true;
        }

        private Dictionary<string, TextBox> methodArgumentsTextboxes = new Dictionary<string,TextBox>();
        private CheckBox signWithConsumerSecretCheckbox;
        private CheckBox signWithTokenSecretCheckbox;
        private TextBox methodResultTextbox;
        private CheckBox methodResultHumanReadableCheckbox;
        private CheckBox useSslCheckbox;

        private void methodsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            /* User clicked a different method in the tree. We will reset and rebuild
             * the right side of the browser window. */

            TreeViewItem selected = (TreeViewItem)methodsTreeView.SelectedItem;
            methodArgumentsTextboxes.Clear();
            mainDockingPanel.Children.Clear();
            if (selected == null)
                return;

            /* The upper panel - for the method call parameters, etc. Docked in the top. */

            var formStackPanel = new StackPanel
            {
                Width = Double.NaN,
                Height = Double.NaN,
                Orientation = Orientation.Vertical,
            };
            var scrollViewer = new ScrollViewer
            {
                Width = Double.NaN,
                Height = getScrollViewerHeight(),
                Margin = new Thickness(0, 0, 0, 10),
            };
            scrollViewer.Content = formStackPanel;
            mainDockingPanel.Children.Add(scrollViewer);
            DockPanel.SetDock(scrollViewer, Dock.Top);

            if (!(selected.Tag is ApiMethod))
                return;

            var method = apiConnector.GetMethod(((ApiMethod)selected.Tag).name);

            /* A header with the method's name. */

            formStackPanel.Children.Add(new Label
            {
                Content = method.brief_description.Replace("_", "__"),
                FontWeight = FontWeights.Bold,
                FontSize = 14
            });

            /* Hyperlink to method's description page. */

            var aBlockWithALink = new TextBlock { Margin = new Thickness(152, 2, 2, 2) };
            var methodDescriptionHyperlink = new Hyperlink { Tag = method.ref_url };
            methodDescriptionHyperlink.Inlines.Add("view full description of this method");
            methodDescriptionHyperlink.Click += methodDescriptionLink_Click;
            aBlockWithALink.Inlines.Add(methodDescriptionHyperlink);
            formStackPanel.Children.Add(aBlockWithALink);

            /* Stacking method arguments textboxes... */

            var arguments = method.arguments;
            if (method.auth_options_token != "ignored")
                arguments.Add(new ApiMethodArgument { name = "as_user_id" });
            foreach (var arg in method.arguments)
            {
                var singleArgumentStackPanel = new StackPanel
                {
                    Width = Double.NaN,
                    Height = Double.NaN,
                    Orientation = Orientation.Horizontal
                };

                /* Adding a label with a name of the argument. */

                singleArgumentStackPanel.Children.Add(new Label
                {
                    Width = 150,
                    Height = 28,
                    Content = arg.name.Replace("_", "__") + ":",
                    FontStyle = (arg.is_required ? FontStyles.Normal : FontStyles.Italic),
                    FontWeight = (arg.is_required ? FontWeights.Bold : FontWeights.Normal),
                });

                /* Adding a textbox for a value. */

                var textbox = new TextBox { Width = 280, Height = 23, Tag = arg, BorderBrush = new SolidColorBrush(Colors.Gray) };
                singleArgumentStackPanel.Children.Add(textbox);

                /* Binding textbox value to cache. This will cause the text to be automatically
                 * filled with a value that was previously entered to it. */

                varsCache.BindWithTextBox(method.name + "#" + arg.name, textbox);

                /* Saving each textbox instance in a dictionary, in order to easily
                 * access it later. */

                methodArgumentsTextboxes.Add(arg.name, textbox);

                /* Stacking the entire thing on the form stack panel. */

                formStackPanel.Children.Add(singleArgumentStackPanel);
            }

            /* Just a small margin. */

            formStackPanel.Children.Add(new Label { Height = 8 });

            /* SSL checkbox. */

            useSslCheckbox = new CheckBox { Content = "Use SSL" + (method.auth_options_ssl_required ? " (required)" : ""),
                Margin = new Thickness(150, 0, 0, 0) };
            formStackPanel.Children.Add(useSslCheckbox);
            varsCache.BindWithCheckBox("use_ssl", useSslCheckbox);

            /* "Sign with..." checkboxes. */

            signWithConsumerSecretCheckbox = new CheckBox { Content = "Sign with Consumer Key (" + method.auth_options_consumer + ")",
                Margin = new Thickness(150, 0, 0, 0) };
            signWithConsumerSecretCheckbox.Checked += consumersigncheckbox_Checked;
            signWithConsumerSecretCheckbox.Unchecked += consumersigncheckbox_Unchecked;
            formStackPanel.Children.Add(signWithConsumerSecretCheckbox);
            signWithTokenSecretCheckbox = new CheckBox { Content = "Sign with Token (" + method.auth_options_token + ")",
                IsEnabled = false, Margin = new Thickness(150, 0, 0, 0) };
            formStackPanel.Children.Add(signWithTokenSecretCheckbox);
            varsCache.BindWithCheckBox("sign_with_consumer_key", signWithConsumerSecretCheckbox);
            varsCache.BindWithCheckBox("sign_with_token", signWithTokenSecretCheckbox);

            /* Execute/Launch buttons. */

            var buttonsStack = new StackPanel
            {
                Width = Double.NaN,
                Height = Double.NaN,
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(150, 0, 0, 0)
            };
            var theExecuteButton = new Button
            {
                Content = "Execute!",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 8, 0, 8)
            };
            theExecuteButton.Click += executeButton_click;
            buttonsStack.Children.Add(theExecuteButton);
            var launchInBrowserButton = new Button
            {
                Content = "Launch in Browser",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(8, 8, 0, 8)
            };
            launchInBrowserButton.Click += browserButton_click;
            buttonsStack.Children.Add(launchInBrowserButton);
            formStackPanel.Children.Add(buttonsStack);

            /* "Try to make it readable" checkbox. */

            methodResultHumanReadableCheckbox = new CheckBox { Content = "Try to make it more human-readable" };
            methodResultHumanReadableCheckbox.Click += executeButton_click;
            varsCache.BindWithCheckBox("make_it_readable", methodResultHumanReadableCheckbox);
            formStackPanel.Children.Add(methodResultHumanReadableCheckbox);

            /* We fill all the rest of the main docking panel with a single textbox - the results. */

            methodResultTextbox = new TextBox
            {
                BorderBrush = new SolidColorBrush(Colors.Gray),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Courier New")
            };
            mainDockingPanel.Children.Add(methodResultTextbox);
        }

        private double getScrollViewerHeight()
        {
            var parentHeight = mainDockingPanel.ActualHeight;
            return parentHeight / 2;
        }

        void consumersigncheckbox_Checked(object sender, RoutedEventArgs e)
        {
            signWithTokenSecretCheckbox.IsEnabled = true;
        }

        void consumersigncheckbox_Unchecked(object sender, RoutedEventArgs e)
        {
            /* When a user unchecks the "sign with consumer key" checkbox, we make
             * the other one (sign with a token) disabled (you can't sign with only
             * a token). */

            signWithTokenSecretCheckbox.IsChecked = false;
            signWithTokenSecretCheckbox.IsEnabled = false;
        }

        void methodDescriptionLink_Click(object sender, RoutedEventArgs e)
        {
            /* User clicked a hyperlink to a method's description. */

            Hyperlink link = (Hyperlink)sender;
            string url = (string)link.Tag;
            try
            {
                Process.Start(url);
            }
            catch (Exception)
            {
                MessageBox.Show("It appears your system doesn't know in which application to open a http:// protocol. Check your browser settings.\n\n" + url);
            }
        }
        
        /// <summary>
        /// Get a dictionary of currently filled argument values of 
        /// a currently displayed method.
        /// </summary>
        Dictionary<string, string> GetMethodArgs()
        {
            Dictionary<string, string> results = new Dictionary<string, string>();
            foreach (var pair in methodArgumentsTextboxes)
            {
                string key = pair.Key;
                string value = pair.Value.Text;
                if (value.Length > 0)
                    results.Add(key, value);
            }
            return results;
        }

        /// <summary>
        /// Get an URL of a currently displayed method, with all the arguments
        /// and signatures applied - according to all the textboxes and checkboxes
        /// in a form.
        /// </summary>
        string GetMethodURL()
        {
            TreeViewItem selected = (TreeViewItem)methodsTreeView.SelectedItem;
            ApiMethod method = (ApiMethod)selected.Tag;
            Dictionary<string, string> args = GetMethodArgs();
            string url = apiConnector.GetURL(method, args,
                signWithConsumerSecretCheckbox.IsChecked == true ? consumerKeyTextbox.Text : "",
                signWithConsumerSecretCheckbox.IsChecked == true ? consumerSecretTextbox.Text : "",
                signWithTokenSecretCheckbox.IsChecked == true ? tokenTextbox.Text : "",
                signWithTokenSecretCheckbox.IsChecked == true ? tokenSecretTextbox.Text : "",
                useSslCheckbox.IsChecked == true);
            return url;
        }

        /// <summary>
        /// User clicks the Execute button.
        /// </summary>
        void executeButton_click(object sender, RoutedEventArgs e)
        {
            try
            {
                string probably_json = apiConnector.GetResponse(GetMethodURL());
                if (methodResultHumanReadableCheckbox.IsChecked == true)
                {
                    try
                    {
                        object? obj = JsonConvert.DeserializeObject(probably_json);
                        if (obj == null)
                            methodResultTextbox.Text = probably_json;
                        else
                            methodResultTextbox.Text = obj.ToString().Replace("\\t", "    ").Replace("\\n", "\n");
                    }
                    catch (JsonReaderException)
                    {
                        methodResultTextbox.Text = probably_json;
                    }
                }
                else
                {
                    methodResultTextbox.Text = probably_json;
                }
            }
            catch (WebException ex)
            {
                methodResultTextbox.Text = ex.Message + "\n" + ApiConnector.ReadResponse(ex.Response);
            }
        }

        /// <summary>
        /// User clicks the "Launch in Browser" button.
        /// </summary>
        void browserButton_click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(GetMethodURL());
            }
            catch (Exception)
            {
                MessageBox.Show("It appears your system doesn't know in which application to open a http:// protocol. Check your browser settings.\n\n" + GetMethodURL());
            }
        }

        /// <summary>
        /// User clicks the "Quick Fill" button.
        /// </summary>
        private void quickFillButton_Click(object sender, RoutedEventArgs e)
        {
            if (consumerKeyTextbox.Text == "")
            {
                if (MessageBox.Show("In order to get Access Tokens, you have to register a Consumer Key first. " +
                    "Would you like to register a new Consumer Key now?", "Consumer Key is missing", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    /* Direct the user to USOS API Developer Center. */
                    try
                    {
                        Process.Start(apiConnector.GetURL(new ApiMethod { name = "developers/" }));
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("It appears your system doesn't know in which application to open a http:// protocol. Check your browser settings.\n\n" + apiConnector.GetURL(new ApiMethod { name = "developers/" }));
                        return;
                    }
                }
                return;
            }

            /* Show initial dialog, user will choose desired scopes. */

            var initialDialog = new QuickFillWindow();
            initialDialog.Owner = this;
            if (initialDialog.ShowDialog() == false)
                return; // user cancelled

            /* Retrieve a list of selected scopes. */

            List<string> scopeKeys = initialDialog.GetSelectedScopeKeys();

            /* Build request_token URL. We will use 'oob' as callback, and
             * require scopes which the user have selected. */

            var request_token_args = new Dictionary<string,string>();
            request_token_args.Add("oauth_callback", "oob");
            if (scopeKeys.Count > 0)
                request_token_args.Add("scopes", string.Join("|", scopeKeys));

            try
            {
                /* Get and parse the request_token response string. */

                string tokenstring;
                try
                {
                    string request_token_url = apiConnector.GetURL(new ApiMethod { name = "services/oauth/request_token" },
                        request_token_args, consumerKeyTextbox.Text, consumerSecretTextbox.Text, "", "", true);
                    tokenstring = apiConnector.GetResponse(request_token_url);
                }
                catch (WebException ex)
                {
                    /* Let's try the same URL, but without SSL. This will allow it to work on
                     * developer installations (which do not support SSL by default). If it still
                     * throws exceptions, pass. */

                    string request_token_url = apiConnector.GetURL(new ApiMethod { name = "services/oauth/request_token" },
                        request_token_args, consumerKeyTextbox.Text, consumerSecretTextbox.Text);
                    tokenstring = apiConnector.GetResponse(request_token_url);
                }
                
                string request_token = null;
                string request_token_secret = null;
                string[] parts = tokenstring.Split('&');
                foreach (string part in parts)
                {
                    if (part.StartsWith("oauth_token="))
                        request_token = part.Substring("oauth_token=".Length);
                    if (part.StartsWith("oauth_token_secret="))
                        request_token_secret = part.Substring("oauth_token_secret=".Length);
                }
                if (request_token == null || request_token_secret == null)
                {
                    MessageBox.Show("Couldn't parse request token. Try to do this sequence manually!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                /* Build authorize URL and open it in user's browser. */

                var authorize_args = new Dictionary<string, string>();
                authorize_args.Add("oauth_token", request_token);
                var authorize_url = apiConnector.GetURL(new ApiMethod { name = "services/oauth/authorize" }, authorize_args);
                try
                {
                    Process.Start(authorize_url);
                }
                catch (Exception)
                {
                    MessageBox.Show("It appears your system doesn't know in which application to open a http:// protocol. Check your browser settings.\n\n" + authorize_url);
                    return;
                }
                
                /* Open a with PIN request and wait for user's entry. */

                var pinWindow = new QuickFillPINWindow();
                pinWindow.Owner = this;
                pinWindow.ShowDialog();
                string verifier = pinWindow.GetPIN();

                /* Build the access_token URL. */

                var access_token_args = new Dictionary<string, string>();
                access_token_args.Add("oauth_verifier", verifier);
                try
                {
                    var access_token_url = apiConnector.GetURL(new ApiMethod { name = "services/oauth/access_token" }, access_token_args,
                        consumerKeyTextbox.Text, consumerSecretTextbox.Text, request_token, request_token_secret, true);
                    tokenstring = apiConnector.GetResponse(access_token_url);
                }
                catch (WebException ex)
                {
                    /* Let's try the same URL, but without SSL. This will allow it to work on
                     * developer installations (which do not support SSL by default). If it still
                     * throws exceptions, pass. */

                    var access_token_url = apiConnector.GetURL(new ApiMethod { name = "services/oauth/access_token" }, access_token_args,
                        consumerKeyTextbox.Text, consumerSecretTextbox.Text, request_token, request_token_secret);
                    tokenstring = apiConnector.GetResponse(access_token_url);
                }
                

                /* Get and parse the access_token response string. */

                string access_token = null;
                string access_token_secret = null;
                parts = tokenstring.Split('&');
                foreach (string part in parts)
                {
                    if (part.StartsWith("oauth_token="))
                        access_token = part.Substring("oauth_token=".Length);
                    if (part.StartsWith("oauth_token_secret="))
                        access_token_secret = part.Substring("oauth_token_secret=".Length);
                }
                if (access_token == null || access_token_secret == null)
                {
                    MessageBox.Show("Couldn't parse access token. Try to do this sequence manually!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                /* Fill up the token textboxes with an Access Token we received. */

                tokenTextbox.Text = access_token;
                tokenSecretTextbox.Text = access_token_secret;
            }
            catch (WebException ex)
            {
                MessageBox.Show("A problem occured. Couldn't complete the Quick Fill.\n\n" + ex.Message + "\n"
                    + ApiConnector.ReadResponse(ex.Response), "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
                
        }

        private void ReloadInstallation()
        {
            methodsTreeView.Items.Clear();
            quickFillButton.IsEnabled = false;
            
            /* Checking which installation is selected in a combo box. */

            if (apiConnector.currentInstallation.base_url != installationsComboBox.Text)
            {
                apiConnector.SwitchInstallation(new ApiInstallation { base_url = installationsComboBox.Text });
            }

            if (!RefreshTree())
                return;
            RefreshScopes();

            /* We did retrieve the list of methods, so the installation URL is OK. If it was
             * entered manually (was not on the installation list in a combo box), then we add
             * it to the list. */

            var onthelist = false;
            foreach (object item in installationsComboBox.Items)
            {
                ApiInstallation itemapi = (ApiInstallation)((ComboBoxItem)item).Tag;
                if (itemapi.base_url == apiConnector.currentInstallation.base_url)
                    onthelist = true;
            }
            if (!onthelist)
            {
                installationsComboBox.Items.Add(new ComboBoxItem
                {
                    Content = apiConnector.currentInstallation.base_url,
                    Tag = apiConnector.currentInstallation
                });
            }
            quickFillButton.IsEnabled = true;

        }

        private void installationRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ReloadInstallation();
        }

        private void installationsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dropdownopen)
            {
                //this.ReloadInstallation();
            }
        }

        private bool dropdownopen;
        private CheckBox runAsUserCheckbox;
        private void installationsComboBox_DropDownClosed(object sender, EventArgs e)
        {
            dropdownopen = false;
        }

        private void installationsComboBox_DropDownOpened(object sender, EventArgs e)
        {
            dropdownopen = true;
        }

        private void installationsComboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ReloadInstallation();
            }
        }
    }
}
