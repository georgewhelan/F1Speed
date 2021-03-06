﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using F1Speed.Core;

namespace F1Speed.Tests
{
    public static class TelemetryLapHelper
    {
        private static float[] lapDistances = new[] { 0.529f, 1.875f, 3.221f, 4.567f, 5.931f, 7.262f, 8.611f, 9.961f, 11.311f, 12.661f, 14.013f, 15.365f, 16.715f, 18.067f, 19.419f };
        private static float[] speeds = new[] { 80.5971f, 80.6278f, 80.738f, 80.7762f, 80.8106f, 80.8426f, 80.878f, 80.9079f, 80.9399f, 80.9709f, 81.0126f, 81.0442f, 81.0747f, 81.1043f, 81.2331f };

        public static TelemetryLap CreatePopulatedLap(float lapNumber, bool completeLap)
        {
            var tLap = new TelemetryLap(Circuit.NullCircuit, "Test");
            const float SampleRate = (1000/60000f);
            
            for (var index = 0; index < lapDistances.Length; index++)
            {
                if (completeLap || index > 0)
                    tLap.AddPacket(new TelemetryPacket { Lap = lapNumber, LapDistance = lapDistances[index], Speed = speeds[index], LapTime=(SampleRate*(index+1) - 0.0001f) });
            }
            
            return tLap;
        }

        public static TelemetryLap CreatePopulatedLap(float lapNumber)
        {
            return CreatePopulatedLap(lapNumber, true);
        }

        public static TelemetryLap CreateOutLap()
        {
            var tLap = new TelemetryLap(Circuit.NullCircuit, "Test");
            const float SampleRate = (1000 / 60000f);

            for (var index = 0; index < lapDistances.Length; index++)
            {
                if (index > 0)
                    tLap.AddPacket(new TelemetryPacket { Lap = 0f, LapDistance = -((20 - lapDistances[index])/20), Speed = speeds[lapDistances.Length - 1 - index], LapTime = (SampleRate * (index + 1) - 0.0001f) });
            }

            return tLap;
        }        
    }
}
