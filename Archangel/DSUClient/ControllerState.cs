using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Archangel.DSUClient
{
    public class ControllerState
    {
        public bool ButtonCross;

        public bool ButtonCircle;

        public bool ButtonSquare;

        public bool ButtonTriangle;

        public bool DPadUp;
        public bool DPadLeft;
        public bool DPadRight;
        public bool DPadDown;

        public bool ButtonSelect;
        public bool ButtonStart;

        public bool ButtonL1;
        public bool ButtonR1;

        public bool ButtonL3;
        public bool ButtonR3;

        public float LeftTrigger { get; set; }
        public float RightTrigger { get; set; }

        public Vector2 LeftStick { get; set; }

        public Vector2 RightStick { get; set; }

        public Vector3 Accelerometer { get; set; }

        public Vector3 Gyroscope { get; set; }
    }
}
