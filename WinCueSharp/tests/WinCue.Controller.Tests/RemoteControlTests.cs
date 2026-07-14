// =============================================================================
// RemoteControlTests.cs — Fase 6: parser OSC y ruteo OSC/MIDI del controlador
// =============================================================================

using System.Buffers.Binary;
using System.Text;
using WinCue.Core;

namespace WinCue.Controller.Tests;

public class OscPacketTests
{
    /// <summary>Construye un mensaje OSC binario (padding y big-endian).</summary>
    internal static byte[] Osc(string address, params object[] args)
    {
        var bytes = new List<byte>();
        void PadString(string s)
        {
            var raw = Encoding.UTF8.GetBytes(s);
            bytes.AddRange(raw);
            int pad = 4 - (raw.Length % 4);
            for (int i = 0; i < pad; i++) bytes.Add(0);
        }
        PadString(address);
        string tags = "," + string.Concat(args.Select(a => a switch
        {
            int => "i", float => "f", string => "s",
            _ => throw new ArgumentException("tipo no soportado"),
        }));
        PadString(tags);
        foreach (object a in args)
        {
            switch (a)
            {
                case int i:
                    var bi = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(bi, i);
                    bytes.AddRange(bi);
                    break;
                case float f:
                    var bf = new byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(bf, f);
                    bytes.AddRange(bf);
                    break;
                case string s:
                    PadString(s);
                    break;
            }
        }
        return bytes.ToArray();
    }

    [Fact]
    public void Parse_SimpleAddress_NoArgs()
    {
        var msgs = OscPacket.Parse(Osc("/go"));
        Assert.Single(msgs);
        Assert.Equal("/go", msgs[0].Address);
        Assert.Empty(msgs[0].Args);
    }

    [Fact]
    public void Parse_IntFloatString_Args()
    {
        var msgs = OscPacket.Parse(Osc("/wincue/cue/5/go", 1, 0.5f, "hola"));
        Assert.Single(msgs);
        Assert.Equal(new object?[] { 1, 0.5f, "hola" }, msgs[0].Args);
    }

    [Fact]
    public void Parse_Bundle_ExtractsAllMessages()
    {
        var m1 = Osc("/go");
        var m2 = Osc("/stop");
        var bundle = new List<byte>();
        bundle.AddRange(Encoding.ASCII.GetBytes("#bundle\0"));
        bundle.AddRange(new byte[8]);   // timetag
        foreach (var m in new[] { m1, m2 })
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, m.Length);
            bundle.AddRange(len);
            bundle.AddRange(m);
        }

        var msgs = OscPacket.Parse(bundle.ToArray());
        Assert.Equal(new[] { "/go", "/stop" }, msgs.Select(m => m.Address));
    }

    [Fact]
    public void Parse_Garbage_ReturnsEmpty()
    {
        Assert.Empty(OscPacket.Parse(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Empty(OscPacket.Parse(Encoding.ASCII.GetBytes("sin barra inicial")));
    }
}

public class RemoteRoutingTests : ControllerTestBase
{
    [Fact]
    public void Osc_Go_FiresPlayheadCue()
    {
        var a = AddCue(CueType.Wait, "a", duration: 5.0);

        Controller.OnOscMessage("/wincue/go", Array.Empty<object?>());

        Assert.Contains(a.Id, Controller.RunningCueIds);
    }

    [Fact]
    public void Osc_ReleaseZero_IsIgnored()
    {
        var a = AddCue(CueType.Wait, "a", duration: 5.0);

        Controller.OnOscMessage("/go", new object?[] { 0 });
        Assert.Empty(Controller.RunningCueIds);

        Controller.OnOscMessage("/go", new object?[] { 1 });
        Assert.Contains(a.Id, Controller.RunningCueIds);
    }

    [Fact]
    public void Osc_CueByNumber_GoAndStop()
    {
        AddCue(CueType.Wait, "uno", duration: 5.0, setup: c => c.Number = "1");
        var dos = AddCue(CueType.Wait, "dos", duration: 5.0, setup: c => c.Number = "2");

        Controller.OnOscMessage("/wincue/cue/2/go", Array.Empty<object?>());
        Assert.Contains(dos.Id, Controller.RunningCueIds);

        Controller.OnOscMessage("/wincue/cue/2/stop", Array.Empty<object?>());
        Assert.DoesNotContain(dos.Id, Controller.RunningCueIds);
    }

    [Fact]
    public void Osc_Panic_StopsEverything()
    {
        AddCue(CueType.Wait, "a", duration: 5.0);
        Controller.OnGoPressed();
        Assert.NotEmpty(Controller.RunningCueIds);

        Controller.OnOscMessage("/panic", Array.Empty<object?>());
        Assert.Empty(Controller.RunningCueIds);
    }

    [Fact]
    public void Midi_GlobalGoNote_FiresPlayheadCue()
    {
        Settings.Set("midi_go_note", 60);
        var a = AddCue(CueType.Wait, "a", duration: 5.0);

        Controller.OnMidiMessage(MidiTriggerType.NoteOn, channel: 1, data1: 60, data2: 127);

        Assert.Contains(a.Id, Controller.RunningCueIds);
    }

    [Fact]
    public void Midi_PerCueTrigger_MatchesChannelAndNote()
    {
        var target = AddCue(CueType.Wait, "target", duration: 5.0, setup: c =>
        {
            c.MidiEnabled = true;
            c.MidiType = MidiTriggerType.NoteOn;
            c.MidiChannel = 3;
            c.MidiNote = 40;
        });

        // Canal equivocado: no dispara
        Controller.OnMidiMessage(MidiTriggerType.NoteOn, channel: 1, data1: 40, data2: 100);
        Assert.Empty(Controller.RunningCueIds);

        Controller.OnMidiMessage(MidiTriggerType.NoteOn, channel: 3, data1: 40, data2: 100);
        Assert.Contains(target.Id, Controller.RunningCueIds);
    }

    [Fact]
    public void Midi_NoteOnVelocityZero_ActsAsNoteOff()
    {
        var target = AddCue(CueType.Wait, "target", duration: 5.0, setup: c =>
        {
            c.MidiEnabled = true;
            c.MidiType = MidiTriggerType.NoteOn;
            c.MidiChannel = 1;
            c.MidiNote = 40;
        });

        Controller.OnMidiMessage(MidiTriggerType.NoteOn, channel: 1, data1: 40, data2: 0);
        Assert.Empty(Controller.RunningCueIds);   // vel 0 = NoteOff, no coincide
    }
}
