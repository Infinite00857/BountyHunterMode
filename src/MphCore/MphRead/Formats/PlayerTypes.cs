using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Effects;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRead.Hud;
using MphRead.Text;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    [Flags]
    public enum LoadFlags : byte
    {
        None = 0,
        Connected = 0x1,
        WasConnected = 0x2,
        Disconnected = 0x4,
        Initial = 0x8,
        Unknown4 = 0x10,
        Active = 0x20,
        SlotActive = 0x40,
        Spawned = 0x80
    }

    public class AvailableArray
    {
        private readonly bool[] _array = new bool[9];

        public void ClearAll()
        {
            for (int i = 0; i < _array.Length; i++)
            {
                _array[i] = false;
            }
        }

        public void SetAll()
        {
            for (int i = 0; i < _array.Length; i++)
            {
                _array[i] = true;
            }
        }

        public void Set(ushort value)
        {
            for (int i = 0; i < 8; i++)
            {
                _array[i] = (value & (1 << i)) != 0;
            }
        }

        public void CopyFrom(AvailableArray source)
        {
            for (int i = 0; i < _array.Length; i++)
            {
                _array[i] = source[i];
            }
        }

        public bool this[int key]
        {
            get => _array[key];
            set => _array[key] = value;
        }

        public bool this[BeamType key]
        {
            get => _array[(int)key];
            set => _array[(int)key] = value;
        }
    }

    public enum PlayerAnimation : sbyte
    {
        None = -1,
        Morph = 0,
        Flourish = 1,
        WalkForward = 2,
        Unmorph = 3,
        DamageBack = 4,
        DamageFront = 5,
        DamageLeft = 6,
        DamageRight = 7,
        Idle = 8,
        LandNeutral = 9,
        LandLeft = 10,
        LandRight = 11,
        JumpNeutral = 12,
        JumpBack = 13,
        JumpForward = 14,
        JumpLeft = 15,
        JumpRight = 16,
        Unused17 = 17,
        WalkBackward = 18,
        Spawn = 19,
        WalkLeft = 20,
        WalkRight = 21,
        Turn = 22,
        Charge = 23,
        ChargeShoot = 24,
        Shoot = 25
    }

    public enum GunAnimation : byte
    {
        FullCharge = 0,
        ChargeShot = 1,
        Charging = 2,
        Idle = 3,
        Switch = 4,
        FullChargeMissile = 5,
        ChargingMissile = 6,
        MissileClose = 7,
        MissileOpen = 8,
        Unknown9 = 9,
        MissileShot = 10,
        UpDown = 11,
        Shot = 12
    }

    public enum TraceAltAnim : byte
    {
        Idle = 0,
        Attack = 1,
        MoveLeft = 2,
        MoveForward = 3,
        MoveRight = 4,
        MoveBackward = 5
    }

    public enum WeavelAltAnim : byte
    {
        Idle = 0,
        Attack = 1,
        MoveLeft = 2,
        MoveForward = 3,
        MoveRight = 4,
        Turn = 5,
        MoveBackward = 6
    }

    public enum KandenAltAnim : byte
    {
        Idle = 0,
        TailOut = 1,
        TailIn = 2
    }

    public enum NoxusAltAnim : byte
    {
        Extend = 0
    }

    public enum SyluxAltAnim : byte
    {
        Idle = 0
    }

    public enum SpireAltAnim : byte
    {
        Attack = 0
    }

}
