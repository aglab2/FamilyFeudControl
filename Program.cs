using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Xml;
using System.Windows.Forms;

namespace Hacktice
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.Run(new Tool());
        }
    }
}
