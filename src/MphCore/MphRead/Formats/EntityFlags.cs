using System;
namespace MphRead.Entities {
    public enum TriggerFlags : uint
    {
        None = 0x0,
        PowerBeam = 0x1,
        VoltDriver = 0x2,
        Missile = 0x4,
        Battlehammer = 0x8,
        Imperialist = 0x10,
        Judicator = 0x20,
        Magmaul = 0x40,
        ShockCoil = 0x80,
        BeamCharged = 0x100,
        PlayerBiped = 0x200,
        PlayerAlt = 0x400,
        Bit11 = 0x800, // unused
        IncludeBots = 0x1000
    }
    public enum FhTriggerFlags : uint
    {
        None = 0x0,
        Beam = 0x1,
        PlayerBiped = 0x2,
        PlayerAlt = 0x4
    }
    public enum ObjectFlags : byte
    {
        None = 0x0,
        StateBit0 = 0x1,
        StateBit1 = 0x2,
        State = 0x3,
        NoAnimation = 0x4,
        EntityLinked = 0x8,
        IsVisible = 0x10
    }
    public enum ObjEffFlags : uint
    {
        None = 0x0,
        UseEffectVolume = 0x1,
        UseEffectOffset = 0x2,
        RepeatScanMessage = 0x4, // todo: send scan message
        WeaponZoom = 0x8,
        AttachEffect = 0x10,
        DestroyEffect = 0x20,
        AlwaysUpdateEffect = 0x40,
        Unknown = 0x8000
    }
    public enum PlatformFlags : uint
    {
        None = 0x0,
        Hazard = 0x1,
        ContactDamage = 0x2,
        BeamSpawner = 0x4,
        BeamColEffect = 0x8,
        DamageReflect1 = 0x10,
        DamageReflect2 = 0x20,
        StandingColOnly = 0x40,
        StartSleep = 0x80,
        SleepAtEnd = 0x100,
        DripMoat = 0x200,
        SkipNodeRef = 0x400,
        DrawIfNodeRef = 0x800,
        DrawAlways = 0x1000,
        HideOnSleep = 0x2000,
        SyluxShip = 0x4000,
        Bit15 = 0x8000,
        BeamReflection = 0x10000,
        UseRoomState = 0x20000,
        BeamTarget = 0x40000,
        SamusShip = 0x80000,
        Breakable = 0x100000,
        PersistRoomState = 0x200000,
        NoBeamIfCull = 0x400000,
        NoRecoil = 0x800000,
        Bit24 = 0x1000000,
        Bit25 = 0x2000000,
        Bit26 = 0x4000000,
        Bit27 = 0x8000000,
        Bit28 = 0x10000000,
        Bit29 = 0x20000000,
        Bit30 = 0x40000000,
        Bit31 = 0x80000000
    }
}
