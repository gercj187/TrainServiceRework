using System;
using System.Collections.Generic;
using System.IO;
using MPAPI.Interfaces.Packets;

namespace TrainServiceRework.Multiplayer
{
    // =========================================================
    // PACKETS
    // =========================================================
    public class ImmersiveServiceConfigPacket : ISerializablePacket
    {
        public bool Enabled;
        public int MaxParallelModules = 2;
        public float[] Seconds = new float[8];
        public bool RefundUnfinished = true;

        public static ImmersiveServiceConfigPacket FromConfig(ImmersiveServiceConfig c)
        {
            return new ImmersiveServiceConfigPacket
            {
                Enabled = c.Enabled,
                MaxParallelModules = c.MaxParallelModules,
                Seconds = new[]
                {
                    c.SecondsFuel, c.SecondsOil, c.SecondsSand, c.SecondsElectric,
                    c.SecondsBody, c.SecondsWheels, c.SecondsMechanical, c.SecondsElectrical
                },
                RefundUnfinished = c.RefundUnfinished
            };
        }

        public ImmersiveServiceConfig ToConfig()
        {
            float S(int i) => Seconds != null && i < Seconds.Length ? Seconds[i] : 0f;

            return new ImmersiveServiceConfig
            {
                Enabled = Enabled,
                MaxParallelModules = MaxParallelModules,
                SecondsFuel = S(0),
                SecondsOil = S(1),
                SecondsSand = S(2),
                SecondsElectric = S(3),
                SecondsBody = S(4),
                SecondsWheels = S(5),
                SecondsMechanical = S(6),
                SecondsElectrical = S(7),
                RefundUnfinished = RefundUnfinished
            };
        }

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Enabled);
            writer.Write(MaxParallelModules);

            float[] seconds = Seconds ?? new float[0];
            writer.Write(seconds.Length);

            for (int i = 0; i < seconds.Length; i++)
                writer.Write(seconds[i]);

            writer.Write(RefundUnfinished);
        }

        public void Deserialize(BinaryReader reader)
        {
            Enabled = reader.ReadBoolean();
            MaxParallelModules = reader.ReadInt32();

            int count = reader.ReadInt32();
            Seconds = new float[Math.Max(0, count)];

            for (int i = 0; i < Seconds.Length; i++)
                Seconds[i] = reader.ReadSingle();

            RefundUnfinished = reader.ReadBoolean();
        }
    }

    public class ImmersiveServiceStatePacket : ISerializablePacket
    {
        public List<KeyValuePair<string, List<ServiceJobView>>> Stations = new List<KeyValuePair<string, List<ServiceJobView>>>();

        public static ImmersiveServiceStatePacket FromSnapshot(List<KeyValuePair<string, List<ServiceJobView>>> snapshot)
        {
            return new ImmersiveServiceStatePacket { Stations = snapshot };
        }

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(Stations.Count);

            for (int s = 0; s < Stations.Count; s++)
            {
                writer.Write(Stations[s].Key ?? string.Empty);

                List<ServiceJobView> views = Stations[s].Value ?? new List<ServiceJobView>();
                writer.Write(views.Count);

                for (int v = 0; v < views.Count; v++)
                {
                    writer.Write(views[v].JobId);
                    writer.Write((byte)views[v].ModuleIndex);
                    writer.Write(views[v].CarGuid ?? string.Empty);
                    writer.Write((byte)views[v].State);
                    writer.Write(views[v].Remaining);
                    writer.Write(views[v].Eta);
                }
            }
        }

        public void Deserialize(BinaryReader reader)
        {
            Stations = new List<KeyValuePair<string, List<ServiceJobView>>>();

            int stationCount = reader.ReadInt32();

            for (int s = 0; s < stationCount; s++)
            {
                string key = reader.ReadString();
                int viewCount = reader.ReadInt32();
                List<ServiceJobView> views = new List<ServiceJobView>(Math.Max(0, viewCount));

                for (int v = 0; v < viewCount; v++)
                {
                    ServiceJobView view = new ServiceJobView
                    {
                        JobId = reader.ReadInt32(),
                        ModuleIndex = reader.ReadByte(),
                        CarGuid = reader.ReadString(),
                        State = (ServiceJobState)reader.ReadByte(),
                        Remaining = reader.ReadSingle(),
                        Eta = reader.ReadSingle()
                    };

                    views.Add(view);
                }

                Stations.Add(new KeyValuePair<string, List<ServiceJobView>>(key, views));
            }
        }
    }
}
