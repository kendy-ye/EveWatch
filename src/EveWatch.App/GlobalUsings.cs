// 本工程同时开了 UseWPF 与 UseWindowsForms，SDK 会为 WinForms 隐式引入 System.Windows.Forms
// 与 System.Drawing，与 WPF 的同名类型冲突。已在 csproj 里把这两个隐式 using 去掉，
// 因此这里不再需要任何别名。
global using System.IO;
