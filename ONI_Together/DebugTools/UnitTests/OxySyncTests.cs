using System;
using System.IO;
using System.Text;
using ONI_Together.Misc;
using ONI_Together.Networking.OxySync.Packets;
using Shared.OxySync;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    public static class OxySyncTests
    {
        [UnitTest(name: "SyncVarPacket round-trip", category: "OxySync")]
        public static UnitTestResult SyncVarPacketRoundTrip()
        {
            var input = new SyncVarPacket
            {
                NetId = 12345,
                FieldHash = "health".GetHashCode(),
                Value = (Variant)100f,
                Timestamp = 987654321098L,
            };

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                input.Serialize(w);

            ms.Position = 0;
            var output = new SyncVarPacket();
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                output.Deserialize(r);

            if (output.NetId != 12345)
                return UnitTestResult.Fail($"NetId mismatch: {output.NetId}");
            if (output.FieldHash != input.FieldHash)
                return UnitTestResult.Fail("FieldHash mismatch");
            if (Mathf.Abs(output.Value.Float - 100f) > 0.001f)
                return UnitTestResult.Fail($"Float value mismatch: {output.Value.Float}");
            if (output.Timestamp != 987654321098L)
                return UnitTestResult.Fail($"Timestamp mismatch: {output.Timestamp}");

            return UnitTestResult.Pass("SyncVarPacket round-trips correctly");
        }

        [UnitTest(name: "SyncVarBatchPacket round-trip", category: "OxySync")]
        public static UnitTestResult SyncVarBatchPacketRoundTrip()
        {
            var updates = new System.Collections.Generic.List<(int Hash, Variant Value)>
            {
                ("hp".GetHashCode(), (Variant)80f),
                ("dead".GetHashCode(), (Variant)false),
                ("name".GetHashCode(), (Variant)"Alice"),
                ("count".GetHashCode(), (Variant)42),
            };

            var input = new SyncVarBatchPacket(999, updates)
            {
                Timestamp = 1234567890123L,
            };

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                input.Serialize(w);

            ms.Position = 0;
            var output = new SyncVarBatchPacket();
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                output.Deserialize(r);

            if (output.NetId != 999)
                return UnitTestResult.Fail($"NetId mismatch: {output.NetId}");
            if (output.Timestamp != 1234567890123L)
                return UnitTestResult.Fail($"Timestamp mismatch: {output.Timestamp}");
            if (output.Count != 4)
                return UnitTestResult.Fail($"Count mismatch: {output.Count}");
            if (output.FieldHashes[0] != updates[0].Hash)
                return UnitTestResult.Fail("Hash 0 mismatch");
            if (Mathf.Abs(output.Values[0].Float - 80f) > 0.001f)
                return UnitTestResult.Fail("Float value 0 mismatch");
            if (output.Values[2].String != "Alice")
                return UnitTestResult.Fail("String value 2 mismatch");
            if (output.Values[3].Int != 42)
                return UnitTestResult.Fail("Int value 3 mismatch");

            return UnitTestResult.Pass("SyncVarBatchPacket round-trips correctly");
        }

        [UnitTest(name: "CommandPacket round-trip", category: "OxySync")]
        public static UnitTestResult CommandPacketRoundTrip()
        {
            var input = new CommandPacket
            {
                NetId = 777,
                MethodHash = "TakeDamage".GetHashCode(),
                Args = new byte[] { 0x01, 0x02, 0x03 },
            };

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                input.Serialize(w);

            ms.Position = 0;
            var output = new CommandPacket();
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                output.Deserialize(r);

            if (output.NetId != 777)
                return UnitTestResult.Fail($"NetId mismatch: {output.NetId}");
            if (output.MethodHash != input.MethodHash)
                return UnitTestResult.Fail("MethodHash mismatch");
            if (output.Args.Length != 3 || output.Args[0] != 0x01)
                return UnitTestResult.Fail("Args mismatch");

            return UnitTestResult.Pass("CommandPacket round-trips correctly");
        }

        [UnitTest(name: "ClientRpcPacket round-trip (broadcast)", category: "OxySync")]
        public static UnitTestResult ClientRpcPacketBroadcastRoundTrip()
        {
            var input = new ClientRpcPacket
            {
                NetId = 555,
                MethodHash = "RpcHealed".GetHashCode(),
                Args = new byte[] { 0x0A },
                TargetPlayerId = ulong.MaxValue,
            };

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                input.Serialize(w);

            ms.Position = 0;
            var output = new ClientRpcPacket();
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                output.Deserialize(r);

            if (output.NetId != 555)
                return UnitTestResult.Fail($"NetId mismatch: {output.NetId}");
            if (output.MethodHash != input.MethodHash)
                return UnitTestResult.Fail("MethodHash mismatch");
            if (output.Args.Length != 1 || output.Args[0] != 0x0A)
                return UnitTestResult.Fail("Args mismatch");
            if (output.TargetPlayerId != ulong.MaxValue)
                return UnitTestResult.Fail("TargetPlayerId should be broadcast");

            return UnitTestResult.Pass("ClientRpcPacket (broadcast) round-trips correctly");
        }

        [UnitTest(name: "ClientRpcPacket round-trip (targeted)", category: "OxySync")]
        public static UnitTestResult ClientRpcPacketTargetedRoundTrip()
        {
            var input = new ClientRpcPacket
            {
                NetId = 444,
                MethodHash = "RpcPrivateMsg".GetHashCode(),
                Args = Array.Empty<byte>(),
                TargetPlayerId = 9001,
            };

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                input.Serialize(w);

            ms.Position = 0;
            var output = new ClientRpcPacket();
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                output.Deserialize(r);

            if (output.TargetPlayerId != 9001)
                return UnitTestResult.Fail($"TargetPlayerId mismatch: {output.TargetPlayerId}");

            return UnitTestResult.Pass("ClientRpcPacket (targeted) round-trips correctly");
        }

        [UnitTest(name: "RpcSerializer all 12 types round-trip", category: "OxySync")]
        public static UnitTestResult RpcSerializerAllTypes()
        {
            object[] args = {
                42,
                3.14f,
                true,
                (byte)7,
                999L,
                2.71828,
                "hello",
                new Vector2(1.5f, 2.5f),
                new Vector3(3f, 4f, 5f),
                new Color(0.1f, 0.2f, 0.3f, 0.4f),
                new Quaternion(0f, 0f, 0f, 1f),
                new byte[] { 0xAA, 0xBB, 0xCC },
            };

            Type[] types = {
                typeof(int), typeof(float), typeof(bool), typeof(byte),
                typeof(long), typeof(double), typeof(string),
                typeof(Vector2), typeof(Vector3), typeof(Color),
                typeof(Quaternion), typeof(byte[]),
            };

            var data = RpcSerializer.Serialize(args, types);
            var result = RpcSerializer.Deserialize(data, types);

            if ((int)result[0] != 42)
                return UnitTestResult.Fail("int mismatch");
            if (Mathf.Abs((float)result[1] - 3.14f) > 0.001f)
                return UnitTestResult.Fail("float mismatch");
            if ((bool)result[2] != true)
                return UnitTestResult.Fail("bool mismatch");
            if ((byte)result[3] != 7)
                return UnitTestResult.Fail("byte mismatch");
            if ((long)result[4] != 999L)
                return UnitTestResult.Fail("long mismatch");
            if (Math.Abs((double)result[5] - 2.71828) > 0.00001)
                return UnitTestResult.Fail("double mismatch");
            if ((string)result[6] != "hello")
                return UnitTestResult.Fail("string mismatch");

            var v2 = (Vector2)result[7];
            if (Mathf.Abs(v2.x - 1.5f) > 0.001f || Mathf.Abs(v2.y - 2.5f) > 0.001f)
                return UnitTestResult.Fail("Vector2 mismatch");

            var v3 = (Vector3)result[8];
            if (Mathf.Abs(v3.x - 3f) > 0.001f || Mathf.Abs(v3.y - 4f) > 0.001f || Mathf.Abs(v3.z - 5f) > 0.001f)
                return UnitTestResult.Fail("Vector3 mismatch");

            var c = (Color)result[9];
            if (Mathf.Abs(c.r - 0.1f) > 0.001f || Mathf.Abs(c.g - 0.2f) > 0.001f)
                return UnitTestResult.Fail("Color mismatch");

            var q = (Quaternion)result[10];
            if (Mathf.Abs(q.w - 1f) > 0.001f)
                return UnitTestResult.Fail("Quaternion mismatch");

            var ba = (byte[])result[11];
            if (ba.Length != 3 || ba[0] != 0xAA || ba[1] != 0xBB || ba[2] != 0xCC)
                return UnitTestResult.Fail("byte[] mismatch");

            return UnitTestResult.Pass("All 12 RPC types round-trip correctly");
        }

        [UnitTest(name: "RpcSerializer empty args", category: "OxySync")]
        public static UnitTestResult RpcSerializerEmptyArgs()
        {
            var data = RpcSerializer.Serialize(Array.Empty<object>(), Array.Empty<Type>());
            var result = RpcSerializer.Deserialize(data, Array.Empty<Type>());

            if (result.Length != 0)
                return UnitTestResult.Fail($"Expected 0 results, got {result.Length}");

            return UnitTestResult.Pass("Empty args round-trip correctly");
        }

        [UnitTest(name: "RpcSerializer string handles null", category: "OxySync")]
        public static UnitTestResult RpcSerializerNullString()
        {
            object[] args = { (string)null };
            Type[] types = { typeof(string) };
            var data = RpcSerializer.Serialize(args, types);
            var result = RpcSerializer.Deserialize(data, types);

            if (result[0] == null)
                return UnitTestResult.Fail("Null string should round-trip as empty string");

            return UnitTestResult.Pass("Null string serializes as empty");
        }

        [UnitTest(name: "Variant all 9 types round-trip", category: "OxySync")]
        public static UnitTestResult VariantAllTypesRoundTrip()
        {
            Variant[] inputs = {
                (Variant)100f,
                (Variant)42,
                (Variant)(byte)7,
                (Variant)"test",
                (Variant)true,
                (Variant)new Vector3(1f, 2f, 3f),
                (Variant)new Vector2(4f, 5f),
                (Variant)new byte[] { 0x01, 0x02 },
                (Variant)new Quaternion(0.1f, 0.2f, 0.3f, 0.4f),
            };

            for (int i = 0; i < inputs.Length; i++)
            {
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    inputs[i].Write(w);

                ms.Position = 0;
                var output = Variant.Read(new BinaryReader(ms, Encoding.UTF8, true));

                switch (inputs[i].Type)
                {
                    case Variant.TypeCode.Float:
                        if (Mathf.Abs(output.Float - 100f) > 0.001f)
                            return UnitTestResult.Fail($"Float variant {i} mismatch");
                        break;
                    case Variant.TypeCode.Int:
                        if (output.Int != 42)
                            return UnitTestResult.Fail($"Int variant {i} mismatch");
                        break;
                    case Variant.TypeCode.Byte:
                        if (output.Byte != 7)
                            return UnitTestResult.Fail($"Byte variant {i} mismatch");
                        break;
                    case Variant.TypeCode.String:
                        if (output.String != "test")
                            return UnitTestResult.Fail($"String variant {i} mismatch");
                        break;
                    case Variant.TypeCode.Boolean:
                        if (output.Boolean != true)
                            return UnitTestResult.Fail($"Bool variant {i} mismatch");
                        break;
                    case Variant.TypeCode.Vector3:
                        if (Mathf.Abs(output.Vector3.x - 1f) > 0.001f)
                            return UnitTestResult.Fail($"Vector3 variant {i} mismatch");
                        break;
                    case Variant.TypeCode.Vector2:
                        if (Mathf.Abs(output.Vector2.x - 4f) > 0.001f)
                            return UnitTestResult.Fail($"Vector2 variant {i} mismatch");
                        break;
                    case Variant.TypeCode.ByteArray:
                        if (output.ByteArray.Length != 2 || output.ByteArray[0] != 0x01)
                            return UnitTestResult.Fail($"byte[] variant {i} mismatch");
                        break;
                    case Variant.TypeCode.Quaternion:
                        var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f);
                        if (Mathf.Abs(output.Quaternion.x - q.x) > 0.001f ||
                            Mathf.Abs(output.Quaternion.y - q.y) > 0.001f ||
                            Mathf.Abs(output.Quaternion.z - q.z) > 0.001f ||
                            Mathf.Abs(output.Quaternion.w - q.w) > 0.001f)
                            return UnitTestResult.Fail($"Quaternion variant {i} mismatch");
                        break;
                }
            }

            return UnitTestResult.Pass("All 9 Variant types round-trip correctly");
        }

        [UnitTest(name: "SyncVarPacket VariantToObject supports all types", category: "OxySync")]
        public static UnitTestResult VariantToObjectAllTypes()
        {
            var testCases = new (Variant V, Type TargetType, object Expected)[]
            {
                ((Variant)42, typeof(int), 42),
                ((Variant)3.14f, typeof(float), 3.14f),
                ((Variant)(byte)7, typeof(byte), (byte)7),
                ((Variant)"hello", typeof(string), "hello"),
                ((Variant)true, typeof(bool), true),
                ((Variant)new Vector3(1,2,3), typeof(Vector3), new Vector3(1,2,3)),
                ((Variant)new Vector2(4,5), typeof(Vector2), new Vector2(4,5)),
                ((Variant)new byte[] { 0x01 }, typeof(byte[]), new byte[] { 0x01 }),
                ((Variant)new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), typeof(Quaternion), new Quaternion(0.1f, 0.2f, 0.3f, 0.4f)),
            };

            foreach (var (v, type, expected) in testCases)
            {
                var result = SyncVarPacket.VariantToObject(v, type);
                if (result == null)
                    return UnitTestResult.Fail($"VariantToObject returned null for {type.Name}");

                if (type == typeof(float))
                {
                    if (Mathf.Abs((float)result - (float)expected) > 0.001f)
                        return UnitTestResult.Fail($"Float conversion mismatch: {result} != {expected}");
                }
                else if (type == typeof(Vector3))
                {
                    var r = (Vector3)result;
                    var e = (Vector3)expected;
                    if (Vector3.Distance(r, e) > 0.001f)
                        return UnitTestResult.Fail($"Vector3 conversion mismatch: {r} != {e}");
                }
                else if (type == typeof(Vector2))
                {
                    var r = (Vector2)result;
                    var e = (Vector2)expected;
                    if (Vector2.Distance(r, e) > 0.001f)
                        return UnitTestResult.Fail($"Vector2 conversion mismatch: {r} != {e}");
                }
                else if (type == typeof(byte[]))
                {
                    var r = (byte[])result;
                    var e = (byte[])expected;
                    if (r.Length != e.Length || r[0] != e[0])
                        return UnitTestResult.Fail($"byte[] conversion mismatch");
                }
                else if (type == typeof(Quaternion))
                {
                    var r = (Quaternion)result;
                    var e = (Quaternion)expected;
                    if (Mathf.Abs(r.x - e.x) > 0.001f || Mathf.Abs(r.y - e.y) > 0.001f ||
                        Mathf.Abs(r.z - e.z) > 0.001f || Mathf.Abs(r.w - e.w) > 0.001f)
                        return UnitTestResult.Fail($"Quaternion conversion mismatch: {r} != {e}");
                }
                else if (!result.Equals(expected))
                {
                    return UnitTestResult.Fail($"{type.Name} conversion mismatch: {result} != {expected}");
                }
            }

            return UnitTestResult.Pass("VariantToObject converts all 9 supported types");
        }

        [UnitTest(name: "VariantToObject null string fallback", category: "OxySync")]
        public static UnitTestResult VariantToObjectNullString()
        {
            var v = new Variant { Type = Variant.TypeCode.String, String = null };
            var result = SyncVarPacket.VariantToObject(v, typeof(string));
            if (result is not string s || s != string.Empty)
                return UnitTestResult.Fail("Null string should become empty string");
            return UnitTestResult.Pass("Null string falls back to empty");
        }

        [UnitTest(name: "VariantToObject null byte[] fallback", category: "OxySync")]
        public static UnitTestResult VariantToObjectNullByteArray()
        {
            var v = new Variant { Type = Variant.TypeCode.ByteArray, ByteArray = null };
            var result = SyncVarPacket.VariantToObject(v, typeof(byte[]));
            if (result is not byte[] ba || ba.Length != 0)
                return UnitTestResult.Fail("Null byte[] should become empty array");
            return UnitTestResult.Pass("Null byte[] falls back to empty");
        }
    }
}
