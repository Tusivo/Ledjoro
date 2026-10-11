using System;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Start-up check for the "everything lives next to the program" layout.</summary>
    static class Portable
    {
        /// <summary>False when the program cannot start (the message has already been shown).</summary>
        public static bool Prepare()
        {
            string problem = Engine.CheckWritable();
            if (problem != null) { MessageBox.Show(problem, "Ledjoro", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            return true;
        }
    }
}
