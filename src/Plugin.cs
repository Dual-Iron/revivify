using BepInEx;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using RWCustom;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Permissions;
using UnityEngine;

// Allows access to private members
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace Revivify;

[BepInPlugin("com.dual.revivify", "Revivify", "1.2.0")]
sealed class Plugin : BaseUnityPlugin
{
    static readonly ConditionalWeakTable<Player, PlayerData> cwt = new();
    static PlayerData Data(Player p) => cwt.GetValue(p, _ => new());

    static PlayerGraphics G(Player p) => p.graphicsModule as PlayerGraphics;

    private static Vector2 HeartPos(Player player)
    {
        return Vector2.Lerp(player.firstChunk.pos, player.bodyChunks[1].pos, 0.38f) + new Vector2(0, 0.7f * player.firstChunk.rad);
    }

    private static bool CanRevive(Player medic, Player reviving)
    {
        if (reviving.playerState.permaDead || !reviving.dead || reviving.grabbedBy.Count > 1 || reviving.Submersion > 0 || reviving.onBack != null
            || Data(reviving).Expired || Data(reviving).deaths >= Options.DeathsUntilExpire.Value
            || !medic.Consious || medic.grabbedBy.Count > 0 || medic.Submersion > 0 || medic.exhausted || medic.lungsExhausted || medic.gourmandExhausted) {
            return false;
        }
        bool corpseStill = reviving.IsTileSolid(0, 0, -1) && reviving.IsTileSolid(1, 0, -1) && reviving.bodyChunks[0].vel.magnitude < 6;
        bool selfStill = medic.input.Take(10).All(i => i.x == 0 && i.y == 0 && !i.thrw && !i.jmp) && medic.bodyChunks[1].ContactPoint.y < 0;
        return corpseStill && selfStill && medic.bodyMode == Player.BodyModeIndex.Stand;
    }

    private static void RevivePlayer(Player self)
    {
        Data(self).deathTime = 0;

        self.stun = 20;
        self.airInLungs = 0.1f;
        self.exhausted = true;
        self.aerobicLevel = 1;

        self.playerState.permanentDamageTracking = Mathf.Clamp01((float)Data(self).deaths / Options.DeathsUntilExhaustion.Value) * 0.6;
        self.playerState.alive = true;
        self.playerState.permaDead = false;
        self.dead = false;
        self.killTag = null;
        self.killTagCounter = 0;
        self.abstractCreature.abstractAI?.SetDestination(self.abstractCreature.pos);
    }

    public void OnEnable()
    {
        On.RainWorld.Update += ErrorCatch;
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;

        new Hook(typeof(Player).GetMethod("get_Malnourished"), getMalnourished);
        On.Player.CanIPutDeadSlugOnBack += Player_CanIPutDeadSlugOnBack;
        On.Player.ctor += Player_ctor;
        On.Player.Die += Player_Die;
        On.HUD.FoodMeter.GameUpdate += FixFoodMeter;
        On.Player.Update += UpdatePlr;
        On.Creature.Violence += ReduceLife;
        On.Player.CanEatMeat += DontEatPlayers;
        On.Player.GraphicsModuleUpdated += DontMoveWhileReviving;
        IL.Player.GrabUpdate += Player_GrabUpdate;

        // Fixes corpse being dropped when pressing Grab
        On.Player.GrabUpdate += FixHeavyCarry;
        On.Player.HeavyCarry += FixHeavyCarry;

        On.PlayerGraphics.Update += PlayerGraphics_Update;
        IL.PlayerGraphics.DrawSprites += ChangeHeadSprite;
        On.PlayerGraphics.DrawSprites += PlayerGraphics_DrawSprites;
        On.SlugcatHand.Update += SlugcatHand_Update;
    }

    private void ErrorCatch(On.RainWorld.orig_Update orig, RainWorld self)
    {
        try {
            orig(self);
        }
        catch (Exception e) {
            Logger.LogError(e);
            throw;
        }
    }

    private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);

        MachineConnector.SetRegisteredOI("revivify", new Options());
    }

    private readonly Func<Func<Player, bool>, Player, bool> getMalnourished = (orig, self) => {
        return orig(self) || Data(self).deaths >= Options.DeathsUntilExhaustion.Value;
    };

    private bool Player_CanIPutDeadSlugOnBack(On.Player.orig_CanIPutDeadSlugOnBack orig, Player self, Player pickUpCandidate)
    {
        return orig(self, pickUpCandidate) || (pickUpCandidate != null && self.slugOnBack != null && !Data(pickUpCandidate).Expired);
    }

    private void Player_ctor(On.Player.orig_ctor orig, Player self, AbstractCreature abstractCreature, World world)
    {
        orig(self, abstractCreature, world);

        if (self.dead) {
            Data(self).expireTime = int.MaxValue;
        }
    }

    private void Player_Die(On.Player.orig_Die orig, Player self)
    {
        if (!self.dead) {
            if (self.drown > 0.25f || self.rainDeath > 0.25f) {
                Data(self).waterInLungs = 1;
            }
            Data(self).deaths++;
        }
        orig(self);
    }

    private void FixFoodMeter(On.HUD.FoodMeter.orig_GameUpdate orig, HUD.FoodMeter self)
    {
        orig(self);

        if (self.IsPupFoodMeter) {
            self.survivalLimit = self.pup.slugcatStats.foodToHibernate;
        }
    }

    private void UpdatePlr(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        const int ticksToDie = 40 * 30; // 30 seconds
        const int ticksToRevive = 40 * 10; // 10 seconds

        if (self.isSlugpup && Data(self).deaths >= Options.DeathsUntilComa.Value) {
            self.stun = 100;
        }

        if (self.dead) {
            ref float death = ref Data(self).deathTime;

            if (death > 0.1f) {
                Data(self).expireTime++;
            }
            else {
                Data(self).expireTime = 0;
            }

            if (death > -0.1f) {
                death += 1f / ticksToDie;
            }
            if (death < -0.5f && self.dangerGrasp == null) {
                death -= 1f / ticksToRevive;

                if (self.room?.shelterDoor != null && self.room.shelterDoor.IsClosing) {
                    death = -1.1f;
                }
            }
            if (death < -1) {
                RevivePlayer(self);
                
                if (self.grabbedBy.FirstOrDefault()?.grabber is Player p) {
                    p.ThrowObject(self.grabbedBy[0].graspUsed, eu);
                }
            }

            death = Mathf.Clamp(death, -1, 1);
        }
        else if (Data(self).waterInLungs > 0 && UnityEngine.Random.value < 1 / 40f && self.Consious) {
            Data(self).waterInLungs -= UnityEngine.Random.value / 4f;

            G(self).breath = Mathf.PI;

            self.Stun(20);
            self.Blink(10);
            self.airInLungs = 0;
            self.firstChunk.pos += self.firstChunk.Rotation * 3;

            int amount = UnityEngine.Random.Range(3, 6);
            for (int i = 0; i < amount; i++) {
                Vector2 dir = Custom.RotateAroundOrigo(self.firstChunk.Rotation, -40f + 80f * UnityEngine.Random.value);

                self.room.AddObject(new WaterDrip(self.firstChunk.pos + dir * 30, dir * (3 + 6 * UnityEngine.Random.value), true));
            }
        }
        else {
            Data(self).deathTime = 0;
        }

        if (Data(self).deaths >= Options.DeathsUntilExhaustion.Value) {
            if (self.isSlugpup) {
                self.slugcatStats.foodToHibernate = self.slugcatStats.maxFood;
            }
            if (self.aerobicLevel >= 1f) {
                Data(self).exhausted = true;
            }
            else if (self.aerobicLevel < 0.3f) {
                Data(self).exhausted = false;
            }
            if (Data(self).exhausted) {
                self.slowMovementStun = Math.Max(self.slowMovementStun, (int)Custom.LerpMap(self.aerobicLevel, 0.7f, 0.4f, 6f, 0f));
                if (self.aerobicLevel > 0.9f && UnityEngine.Random.value < 0.04f) {
                    self.Stun(10);
                }
                if (self.aerobicLevel > 0.9f && UnityEngine.Random.value < 0.1f) {
                    self.standing = false;
                }
                if (!(self.lungsExhausted && self.animation != Player.AnimationIndex.SurfaceSwim)) {
                    self.swimCycle += 0.05f;
                }
            }
        }
    }

    private void ReduceLife(On.Creature.orig_Violence orig, Creature self, BodyChunk source, Vector2? directionAndMomentum, BodyChunk hitChunk, PhysicalObject.Appendage.Pos hitAppendage, Creature.DamageType type, float damage, float stunBonus)
    {
        bool wasDead = self.dead;

        orig(self, source, directionAndMomentum, hitChunk, hitAppendage, type, damage, stunBonus);

        if (self is Player p && wasDead && p.dead && damage > 0) {
            PlayerData data = Data(p);
            if (data.deathTime < 0) {
                data.deathTime = 0;
            }
            data.deathTime += damage * 0.34f;
        }
    }

    private bool DontEatPlayers(On.Player.orig_CanEatMeat orig, Player self, Creature crit)
    {
        return crit is not Player && orig(self, crit);
    }

    private void DontMoveWhileReviving(On.Player.orig_GraphicsModuleUpdated orig, Player self, bool actuallyViewed, bool eu)
    {
        Vector2 pos1 = default, pos2 = default, vel1 = default, vel2 = default;
        Vector2 posH = default, posB = default, velH = default, velB = default;

        foreach (var grasp in self.grasps) {
            if (grasp?.grabbed is Player p && CanRevive(self, p)) {
                posH = self.bodyChunks[0].pos;
                posB = self.bodyChunks[1].pos;
                velH = self.bodyChunks[0].vel;
                velB = self.bodyChunks[1].vel;

                pos1 = p.bodyChunks[0].pos;
                pos2 = p.bodyChunks[1].pos;
                vel1 = p.bodyChunks[0].vel;
                vel2 = p.bodyChunks[1].vel;
                break;
            }
        }

        orig(self, actuallyViewed, eu);

        if (pos1 != default) {
            foreach (var grasp in self.grasps) {
                if (grasp?.grabbed is Player p && CanRevive(self, p)) {
                    self.bodyChunks[0].pos = posH;
                    self.bodyChunks[1].pos = posB;
                    self.bodyChunks[0].vel = velH;
                    self.bodyChunks[1].vel = velB;

                    p.bodyChunks[0].pos = pos1;
                    p.bodyChunks[1].pos = pos2;
                    p.bodyChunks[0].vel = vel1;
                    p.bodyChunks[1].vel = vel2;
                    break;
                }
            }
        }
    }

    private void Player_GrabUpdate(ILContext il)
    {
        try {
            ILCursor cursor = new(il);

            // Move after num11 check and ModManager.MSC
            cursor.GotoNext(MoveType.After, i => i.MatchStloc(8));
            cursor.Index++;
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldloc_S, il.Body.Variables[8]);
            cursor.EmitDelegate(UpdateRevive);
            cursor.Emit(OpCodes.Brfalse, cursor.Next);
            cursor.Emit(OpCodes.Pop); // pop "ModManager.MSC" off stack
            cursor.Emit(OpCodes.Ret);
        }
        catch (Exception e) {
            Logger.LogError(e);
        }
    }

    private bool UpdateRevive(Player self, int grasp)
    {
        PlayerData data = Data(self);

        if (self.grasps[grasp]?.grabbed is not Player reviving || !CanRevive(self, reviving)) {
            data.Unprepared();
            return false;
        }

        Vector2 heartPos = HeartPos(reviving);
        Vector2 targetHeadPos = heartPos + new Vector2(0, Mathf.Sign(self.room.gravity)) * 25;
        Vector2 targetButtPos = heartPos - new Vector2(0, reviving.bodyChunks[0].rad);
        float headDist = (targetHeadPos - self.bodyChunks[0].pos).magnitude;
        float buttDist = (targetButtPos - self.bodyChunks[1].pos).magnitude;

        if (data.animTime < 0 && (headDist > 22 || buttDist > 22)) {
            return false;
        }

        self.bodyChunks[0].vel += Mathf.Min(headDist, 0.4f) * (targetHeadPos - self.bodyChunks[0].pos).normalized;
        self.bodyChunks[1].vel += Mathf.Min(buttDist, 0.4f) * (targetButtPos - self.bodyChunks[1].pos).normalized;

        PlayerData revivingData = Data(reviving);
        int difference = self.room.game.clock - revivingData.lastCompression;

        if (data.animTime < 0) {
            data.PreparedToGiveCpr();
        }
        else if (self.input[0].pckp && !self.input[1].pckp && difference > 4) {
            Compression(self, grasp, data, reviving, revivingData, difference);
        }

        AnimationStage stage = data.Stage();

        if (stage is AnimationStage.Prepared or AnimationStage.CompressionRest) {
            self.bodyChunkConnections[0].distance = 14;
        }
        if (stage is AnimationStage.CompressionDown) {
            self.bodyChunkConnections[0].distance = 13 - data.compressionDepth;
        }
        if (stage is AnimationStage.CompressionUp) {
            self.bodyChunkConnections[0].distance = Mathf.Lerp(13 - data.compressionDepth, 15, (data.animTime - 3) / 2f);
        }

        if (data.animTime > 0) {
            data.animTime++;
        }
        if (Data(reviving).compressionsUntilBreath > 0) {
            if (data.animTime >= 20)
                data.PreparedToGiveCpr();
        }
        else if (data.animTime >= 80) {
            data.PreparedToGiveCpr();
        }

        return false;
    }

    private static bool disableHeavyCarry = false;
    private void FixHeavyCarry(On.Player.orig_GrabUpdate orig, Player self, bool eu)
    {
        try {
            disableHeavyCarry = true;
            orig(self, eu);
        }
        finally {
            disableHeavyCarry = false;
        }
    }
    private bool FixHeavyCarry(On.Player.orig_HeavyCarry orig, Player self, PhysicalObject obj)
    {
        return !(disableHeavyCarry && obj is Player p && CanRevive(self, p)) && orig(self, obj);
    }

    private static void Compression(Player self, int grasp, PlayerData data, Player reviving, PlayerData revivingData, int difference)
    {
        if (self.slugOnBack != null) {
            self.slugOnBack.interactionLocked = true;
            self.slugOnBack.counter = 0;
        }

        if (self.grasps[grasp].chunkGrabbed == 1) {
            self.grasps[grasp].chunkGrabbed = 0;
        }

        if (reviving.AI != null) {
            reviving.State.socialMemory.GetOrInitiateRelationship(self.abstractCreature.ID).InfluenceLike(10f);
            reviving.State.socialMemory.GetOrInitiateRelationship(self.abstractCreature.ID).InfluenceTempLike(10f);
            reviving.State.socialMemory.GetOrInitiateRelationship(self.abstractCreature.ID).InfluenceKnow(0.5f);
        }

        for (int i = reviving.abstractCreature.stuckObjects.Count - 1; i >= 0; i--) {
            if (reviving.abstractCreature.stuckObjects[i] is AbstractPhysicalObject.AbstractSpearStick stick && stick.A.realizedObject is Spear s) {
                s.ChangeMode(Weapon.Mode.Free);
            }
        }

        data.StartCompression();
        self.AerobicIncrease(0.5f);

        revivingData.compressionsUntilBreath--;
        if (revivingData.compressionsUntilBreath < 0) {
            revivingData.compressionsUntilBreath = 1000;//(int)(8 + UnityEngine.Random.value * 5);
        }

        bool breathing = revivingData.compressionsUntilBreath == 0;
        float healing = difference switch {
            < 75 when breathing => -1 / 10f,
            < 100 when breathing => 1 / 5f,
            < 8 => -1 / 30f,
            < 19 => 1 / 40f,
            < 22 => 1 / 15f,
            < 30 => 1 / 20f,
            _ => 1 / 40f,
        };
        data.compressionDepth = difference switch {
            < 8 => 0.2f,
            < 19 => 1f,
            < 22 => 4.5f,
            < 30 => 3.5f,
            _ => 1f
        };
        if (data.compressionDepth > 4) self.Blink(6);
        revivingData.deathTime -= healing * Options.ReviveSpeed.Value;
        revivingData.lastCompression = self.room.game.clock;

        if (revivingData.waterInLungs > 0) {
            revivingData.waterInLungs -= healing * 0.34f;

            float amount = data.compressionDepth * 0.5f + UnityEngine.Random.value - 0.5f;
            for (int i = 0; i < amount; i++) {
                Vector2 dir = Custom.RotateAroundOrigo(new Vector2(0, 1), -30f + 60f * UnityEngine.Random.value);

                reviving.room.AddObject(new WaterDrip(reviving.firstChunk.pos + dir * 10, dir * (2 + 4 * UnityEngine.Random.value), true));
            }
        }
    }

    private void PlayerGraphics_Update(On.PlayerGraphics.orig_Update orig, PlayerGraphics self)
    {
        orig(self);

        PlayerData data = Data(self.player);

        float visualDecay = Mathf.Max(Mathf.Clamp01(data.deathTime), Mathf.Clamp01((float)data.deaths / Options.DeathsUntilExhaustion.Value) * 0.6f);
        if (self.malnourished < visualDecay) {
            self.malnourished = visualDecay;
        }

        if (self.player.grasps.FirstOrDefault(g => g?.grabbed is Player)?.grabbed is not Player reviving) {
            return;
        }

        AnimationStage stage = data.Stage();

        if (stage == AnimationStage.None) return;

        Vector2 starePos = stage is AnimationStage.Prepared or AnimationStage.CompressionDown or AnimationStage.CompressionUp or AnimationStage.CompressionRest
            ? HeartPos(reviving)
            : reviving.firstChunk.pos + reviving.firstChunk.Rotation * 5;

        self.LookAtPoint(starePos, 10000f);

        if (stage is AnimationStage.CompressionDown) {
            // Push reviving person's head and butt upwards
            PlayerGraphics graf = G(reviving);
            graf.head.vel.y += data.compressionDepth * 0.5f;
            graf.NudgeDrawPosition(0, new(0, data.compressionDepth * 0.5f));
            if (graf.tail.Length > 1) {
                graf.tail[0].pos.y += 1;
                graf.tail[0].vel.y += data.compressionDepth * 0.8f;
                graf.tail[1].vel.y += data.compressionDepth * 0.2f;
            }
        }
    }

    private void ChangeHeadSprite(ILContext il)
    {
        try {
            ILCursor cursor = new(il);

            // Move after num11 check and ModManager.MSC
            cursor.GotoNext(MoveType.Before, i => i.MatchCall<PlayerGraphics>("get_RenderAsPup"));
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldloca, il.Body.Variables[9]);
            cursor.EmitDelegate(ChangeHead);
        }
        catch (Exception e) {
            Logger.LogError(e);
        }
    }

    private void ChangeHead(PlayerGraphics self, ref int headNum)
    {
        if (self.player.grabbedBy.Count == 1 && self.player.grabbedBy[0].grabber is Player medic && CanRevive(medic, self.player) && Data(medic).animTime >= 0) {
            headNum = 7;
        }
    }

    private void PlayerGraphics_DrawSprites(On.PlayerGraphics.orig_DrawSprites orig, PlayerGraphics self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        orig(self, sLeaser, rCam, timeStacker, camPos);

        if (self.player.grabbedBy.Count == 1 && self.player.grabbedBy[0].grabber is Player medic && CanRevive(medic, self.player) && Data(medic).animTime >= 0) {
            sLeaser.sprites[9].y += 6;
            sLeaser.sprites[3].rotation -= 50 * Mathf.Sign(sLeaser.sprites[3].rotation);
            sLeaser.sprites[3].scaleX *= -1;
        }

        if (sLeaser.sprites[9].element.name == "FaceDead" && Data(self.player).deathTime < -0.6f) {
            sLeaser.sprites[9].element = Futile.atlasManager.GetElementWithName("FaceStunned");
        }
    }

    private void SlugcatHand_Update(On.SlugcatHand.orig_Update orig, SlugcatHand self)
    {
        orig(self);

        Player player = ((PlayerGraphics)self.owner).player;
        PlayerData data = Data(player);

        if (player.grasps.FirstOrDefault(g => g?.grabbed is Player)?.grabbed is not Player reviving) {
            return;
        }

        AnimationStage stage = data.Stage();

        if (stage == AnimationStage.None) return;

        Vector2 heart = HeartPos(reviving);
        Vector2 heartDown = HeartPos(reviving) - new Vector2(0, data.compressionDepth);

        if (stage is AnimationStage.Prepared or AnimationStage.CompressionRest) {
            self.pos = heart;
        }
        else if (stage == AnimationStage.CompressionDown) {
            self.pos = heartDown;
        }
        else if (stage == AnimationStage.CompressionUp) {
            self.pos = Vector2.Lerp(heartDown, heart, (data.animTime - 3) / 2f);
        }
    }
}
