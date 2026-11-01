using System;
namespace MDNote {public partial class MainWindow {
static string Canonical(string value){return value.Replace("\r\n","\n").Replace('\r','\n');}
static int ActualSourceOffset(string value,int canonical){int at=0,count=0;while(at<value.Length&&count<canonical){if(value[at]=='\r'&&at+1<value.Length&&value[at+1]=='\n')at++;at++;count++;}return at;}
}}
