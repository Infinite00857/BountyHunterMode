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
    public enum DamageFlags : int
    {
        None = 0,
        NoDmgInvuln = 1,
        IgnoreInvuln = 2,
        Death = 4,
        Halfturret = 8,
        Headshot = 0x10,
        Deathalt = 0x20,
        Burn = 0x40,
        NoSfx = 0x80,
        FromAlt = 0x100
    }

    [Flags]
    public enum PlayerFlags1 : uint
    {
        None = 0,
        Standing = 1,
        StandingPrevious = 2,
        NoUnmorph = 4,
        NoUnmorphPrevious = 8,
        CollidingLateral = 0x10,
        OnAcid = 0x20,
        OnLava = 0x40,
        CollidingEntity = 0x80,
        UsedJump = 0x100,
        AltForm = 0x200,
        AltFormPrevious = 0x400,
        Morphing = 0x800,
        Unmorphing = 0x1000,
        Strafing = 0x2000,
        FreeLook = 0x4000,
        FreeLookPrevious = 0x8000,
        ShotUncharged = 0x10000,
        ShotMissile = 0x20000,
        ShotCharged = 0x40000,
        GunOpenAnimation = 0x80000,
        Grounded = 0x100000,
        GroundedPrevious = 0x200000,
        Walking = 0x400000,
        MovingBiped = 0x800000,
        NoAimInput = 0x1000000,
        WeaponMenuOpen = 0x2000000,
        Boosting = 0x4000000,
        CanTouchBoost = 0x8000000,
        UsedJumpPad = 0x10000000,
        AltDirOverride = 0x20000000,
        Bit30 = 0x40000000,
        DrawGunSmoke = 0x80000000
    }

    [Flags]
    public enum PlayerFlags2 : uint
    {
        None = 0,
        ChargeEffect = 1,
        HideModel = 2,
        Shooting = 4,
        AltAttack = 8,
        BipedStuck = 0x10,
        Halfturret = 0x20,
        Cloaking = 0x40,
        GravityOverride = 0x80,
        NoFormSwitch = 0x100,
        BipedLock = 0x200,
        AltFormGravity = 0x400,
        Lod1 = 0x800,
        DrawnThirdPerson = 0x1000,
        RadarReveal = 0x2000,
        RadarRevealPrevious = 0x4000,
        SpireClimbing = 0x8000,
        NoShotsFired = 0x10000,
        UnequipOmegaCannon = 0x20000
    }

    [Flags]
    public enum AbilityFlags : short
    {
        None = 0,
        AltForm = 1,
        SpaceJump = 2,
        Bombs = 4,
        Boost = 0x40,
        NoxusAltAttack = 0x100,
        SpireAltAttack = 0x200,
        TraceAltAttack = 0x400,
        WeavelAltAttack = 0x1000
    }

    [Flags]
    public enum CrushFlags : byte
    {
        None = 0,
        Bit0 = 1,
        Bit1 = 2,
        Bit2 = 4,
        Bit3 = 8,
        Bit4 = 0x10,
        Bit5 = 0x20
    }

    public readonly struct PlayerValues
    {
        public readonly Hunter Hunter; // the game doesn't have this
        public readonly int WalkBipedTraction;
        public readonly int StrafeBipedTraction;
        public readonly int WalkSpeedCap;
        public readonly int StrafeSpeedCap;
        public readonly int AltMinHSpeed;
        public readonly int BoostSpeedCap;
        public readonly int BipedGravity;
        public readonly int AltAirGravity;
        public readonly int AltGroundGravity;
        public readonly int JumpSpeed;
        public readonly int WalkSpeedFactor;
        public readonly int AltGroundSpeedFactor;
        public readonly int StrafeSpeedFactor;
        public readonly int AirSpeedFactor;
        public readonly int StandSpeedFactor;
        public readonly int RollAltTraction;
        public readonly int AltColRadius;
        public readonly int AltColYPos;
        public readonly ushort BoostChargeMin;
        public readonly ushort BoostChargeMax;
        public readonly int BoostSpeedMin;
        public readonly int BoostSpeedMax;
        public readonly int AltHSpeedCapIncrement;
        public readonly int Field58;
        public readonly int Field5C;
        public readonly int WalkBobMax;
        public readonly int AimDistance;
        public readonly ushort CamSwitchTime;
        public readonly ushort Padding6A;
        public readonly int NormalFov;
        public readonly int Field70;
        public readonly int AimYOffset;
        public readonly int Field78;
        public readonly int Field7C;
        public readonly int Field80;
        public readonly int Field84;
        public readonly int Field88;
        public readonly int Field8C;
        public readonly int Field90;
        public readonly int MinPickupHeight;
        public readonly int MaxPickupHeight;
        public readonly int BipedColRadius;
        public readonly int FieldA0;
        public readonly int FieldA4;
        public readonly int FieldA8;
        public readonly short DamageInvuln;
        public readonly ushort DamageFlashTime;
        public readonly int FieldB0;
        public readonly int FieldB4;
        public readonly int FieldB8;
        public readonly int MuzzleOffset;
        public readonly int BombCooldown;
        public readonly int BombSelfRadius;
        public readonly int BombSelfRadiusSquared;
        public readonly int BombRadius;
        public readonly int BombRadiusSquared;
        public readonly int BombJumpSpeed;
        public readonly int BombRefillTime;
        public readonly short BombDamage;
        public readonly short BombEnemyDamage;
        public readonly short FieldE0;
        public readonly short SpawnInvulnerability;
        public readonly ushort AimMinTouchTime;
        public readonly ushort PaddingE6;
        public readonly int FieldE8;
        public readonly int FieldEC;
        public readonly int SwayStartTime;
        public readonly int SwayIncrement;
        public readonly int SwayLimit;
        public readonly int GunIdleTime;
        public readonly short MpAmmoCap;
        public readonly byte AmmoRecharge;
        public readonly byte Padding103;
        public readonly ushort EnergyTank;
        public readonly short Field106;
        public readonly byte AltFormStrafe;
        public readonly byte Padding109;
        public readonly ushort Padding10A;
        public readonly int FallDamageSpeed;
        public readonly int FallDamageMax;
        public readonly int Field114;
        public readonly int Field118;
        public readonly int JumpPadSlideFactor;
        public readonly int AltTiltAngleCap;
        public readonly int AltMinWobble;
        public readonly int AltMaxWobble;
        public readonly int AltMinSpinAccel;
        public readonly int AltMaxSpinAccel;
        public readonly int AltMinSpinSpeed;
        public readonly int AltMaxSpinSpeed;
        public readonly int AltTiltAngleMax;
        public readonly int AltBounceWobble;
        public readonly int AltBounceTilt;
        public readonly int AltBounceSpin;
        public readonly int AltAttackKnockbackAccel;
        public readonly short AltAttackKnockbackTime;
        public readonly ushort AltAttackStartup;
        public readonly int Field154;
        public readonly int Field158;
        public readonly int LungeHSpeed;
        public readonly int LungeVSpeed;
        public readonly ushort AltAttackDamage;
        public readonly short AltAttackCooldown;

        public PlayerValues(Hunter hunter, int walkBipedTraction, int strafeBipedTraction, int walkSpeedCap, int strafeSpeedCap,
            int altMinHSpeed, int boostSpeedCap, int bipedGravity, int altAirGravity, int altGroundGravity, int jumpSpeed, int walkSpeedFactor,
            int altGroundSpeedFactor, int strafeSpeedFactor, int airSpeedFactor, int standSpeedFactor, int rollAltTraction, int altColRadius,
            int altColYPos, ushort boostChargeMin, ushort boostChargeMax, int boostSpeedMin, int boostSpeedMax, int altHSpeedCapIncrement,
            int field58, int field5C, int walkBobMax, int aimDistance, ushort camSwitchTime, ushort padding6A, int normalFov, int field70,
            int aimYOffset, int field78, int field7C, int field80, int field84, int field88, int field8C, int field90, int minPickupHeight,
            int maxPickupHeight, int bipedColRadius, int fieldA0, int fieldA4, int fieldA8, short damageInvuln, ushort damageFlashTime,
            int fieldB0, int fieldB4, int fieldB8, int muzzleOffset, int bombCooldown, int bombSelfRadius, int bombSelfRadiusSquared,
            int bombRadius, int bombRadiusSquared, int bombJumpSpeed, int bombRefillTime, short bombDamage, short bombEnemyDamage,
            short fieldE0, short spawnInvulnerability, ushort aimMinTouchTime, ushort paddingE6, int fieldE8, int fieldEC, int swayStartTime,
            int swayIncrement, int swayLimit, int gunIdleTime, short mpAmmoCap, byte ammoRecharge, byte padding103, ushort energyTank,
            short field106, byte altFormStrafe, byte padding109, ushort padding10A, int fallDamageSpeed, int fallDamageMax, int field114,
            int field118, int jumpPadSlideFactor, int altTiltAngleCap, int altMinWobble, int altMaxWobble, int altMinSpinAccel, int altMaxSpinAccel,
            int altMinSpinSpeed, int altMaxSpinSpeed, int altTiltAngleMax, int altBounceWobble, int altBounceTilt, int altBounceSpin,
            int altAttackKnockbackAccel, short altAttackKnockbackTime, ushort altAttackStartup, int field154, int field158, int lungeHSpeed,
            int lungeVSpeed, ushort altAttackDamage, short altAttackCooldown)
        {
            Hunter = hunter;
            WalkBipedTraction = walkBipedTraction;
            StrafeBipedTraction = strafeBipedTraction;
            WalkSpeedCap = walkSpeedCap;
            StrafeSpeedCap = strafeSpeedCap;
            AltMinHSpeed = altMinHSpeed;
            BoostSpeedCap = boostSpeedCap;
            BipedGravity = bipedGravity;
            AltAirGravity = altAirGravity;
            AltGroundGravity = altGroundGravity;
            JumpSpeed = jumpSpeed;
            WalkSpeedFactor = walkSpeedFactor;
            AltGroundSpeedFactor = altGroundSpeedFactor;
            StrafeSpeedFactor = strafeSpeedFactor;
            AirSpeedFactor = airSpeedFactor;
            StandSpeedFactor = standSpeedFactor;
            RollAltTraction = rollAltTraction;
            AltColRadius = altColRadius;
            AltColYPos = altColYPos;
            BoostChargeMin = boostChargeMin;
            BoostChargeMax = boostChargeMax;
            BoostSpeedMin = boostSpeedMin;
            BoostSpeedMax = boostSpeedMax;
            AltHSpeedCapIncrement = altHSpeedCapIncrement;
            Field58 = field58;
            Field5C = field5C;
            WalkBobMax = walkBobMax;
            AimDistance = aimDistance;
            CamSwitchTime = camSwitchTime;
            Padding6A = padding6A;
            NormalFov = normalFov;
            Field70 = field70;
            AimYOffset = aimYOffset;
            Field78 = field78;
            Field7C = field7C;
            Field80 = field80;
            Field84 = field84;
            Field88 = field88;
            Field8C = field8C;
            Field90 = field90;
            MinPickupHeight = minPickupHeight;
            MaxPickupHeight = maxPickupHeight;
            BipedColRadius = bipedColRadius;
            FieldA0 = fieldA0;
            FieldA4 = fieldA4;
            FieldA8 = fieldA8;
            DamageInvuln = damageInvuln;
            DamageFlashTime = damageFlashTime;
            FieldB0 = fieldB0;
            FieldB4 = fieldB4;
            FieldB8 = fieldB8;
            MuzzleOffset = muzzleOffset;
            BombCooldown = bombCooldown;
            BombSelfRadius = bombSelfRadius;
            BombSelfRadiusSquared = bombSelfRadiusSquared;
            BombRadius = bombRadius;
            BombRadiusSquared = bombRadiusSquared;
            BombJumpSpeed = bombJumpSpeed;
            BombRefillTime = bombRefillTime;
            BombDamage = bombDamage;
            BombEnemyDamage = bombEnemyDamage;
            FieldE0 = fieldE0;
            SpawnInvulnerability = spawnInvulnerability;
            AimMinTouchTime = aimMinTouchTime;
            PaddingE6 = paddingE6;
            FieldE8 = fieldE8;
            FieldEC = fieldEC;
            SwayStartTime = swayStartTime;
            SwayIncrement = swayIncrement;
            SwayLimit = swayLimit;
            GunIdleTime = gunIdleTime;
            MpAmmoCap = mpAmmoCap;
            AmmoRecharge = ammoRecharge;
            Padding103 = padding103;
            EnergyTank = energyTank;
            Field106 = field106;
            AltFormStrafe = altFormStrafe;
            Padding109 = padding109;
            Padding10A = padding10A;
            FallDamageSpeed = fallDamageSpeed;
            FallDamageMax = fallDamageMax;
            Field114 = field114;
            Field118 = field118;
            JumpPadSlideFactor = jumpPadSlideFactor;
            AltTiltAngleCap = altTiltAngleCap;
            AltMinWobble = altMinWobble;
            AltMaxWobble = altMaxWobble;
            AltMinSpinAccel = altMinSpinAccel;
            AltMaxSpinAccel = altMaxSpinAccel;
            AltMinSpinSpeed = altMinSpinSpeed;
            AltMaxSpinSpeed = altMaxSpinSpeed;
            AltTiltAngleMax = altTiltAngleMax;
            AltBounceWobble = altBounceWobble;
            AltBounceTilt = altBounceTilt;
            AltBounceSpin = altBounceSpin;
            AltAttackKnockbackAccel = altAttackKnockbackAccel;
            AltAttackKnockbackTime = altAttackKnockbackTime;
            AltAttackStartup = altAttackStartup;
            Field154 = field154;
            Field158 = field158;
            LungeHSpeed = lungeHSpeed;
            LungeVSpeed = lungeVSpeed;
            AltAttackDamage = altAttackDamage;
            AltAttackCooldown = altAttackCooldown;
        }
    }
}
