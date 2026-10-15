namespace AORebirth.Enums
{
    /// <summary>
    /// AmmoType (stat 420) on weapons and their ammo. Names follow the ammo items and weapons that carry each value
    /// in items.dat. 7 (only Michizure's Boomstick! 295603) is not named.
    /// </summary>
    public enum AmmoType : int
    {
        None = 0,
        Energy = 1,
        Bullets = 2,
        Flamethrower = 3,
        ShotgunShells = 4,
        Arrows = 5,
        Grenades = 6,

        /// <summary>Ammo: Antivehicle Rockets, the Rocket Launchers and the Anti-Vehicular Missile Launcher.</summary>
        Rockets = 8,

        /// <summary>Weapons that need no ammo item: Solar-Powered weapons, Training Pistol.</summary>
        SelfPowered = 10,
    }
}
