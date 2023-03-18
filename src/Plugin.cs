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

[BepInPlugin("com.dual.revivify", "Revivify", "1.0.0")]
sealed class Plugin : BaseUnityPlugin
{
    static readonly Player.AnimationIndex ReviveAnimation = new("Revive", register: true);
    static readonly ConditionalWeakTable<Player, PlayerData> cwt = new();
    static PlayerData Data(Player p) => cwt.GetValue(p, _ => new());

    static PlayerGraphics G(Player p) => p.graphicsModule as PlayerGraphics;

    private static Vector2 HeartPos(Player player)
    {
        return Vector2.Lerp(player.firstChunk.pos, player.bodyChunks[1].pos, 0.35f) + new Vector2(0, 0.8f * player.firstChunk.rad);
    }

    private static Vector2 HeartPosCompressed(Player player)
    {
        return Vector2.Lerp(player.firstChunk.pos, player.bodyChunks[1].pos, 0.35f) + new Vector2(0, 0.1f * player.firstChunk.rad);
    }

    private static bool CanRevive(Player medic, Player reviving)
    {
        if (reviving.playerState.permaDead || !reviving.dead || reviving.grabbedBy.Count > 1 || reviving.Submersion > 0 || Data(reviving).DeadForReal
            || !medic.Consious || medic.grabbedBy.Count > 0 || medic.Submersion > 0 || medic.exhausted || medic.lungsExhausted || medic.gourmandExhausted) {
            return false;
        }
        bool corpseStill = reviving.bodyChunks[0].ContactPoint.y < 0 && reviving.bodyChunks[1].ContactPoint.y < 0 
            && (Data(medic).animationTime >= 40 || reviving.bodyChunks[0].vel.magnitude < 2 && reviving.bodyChunks[1].vel.magnitude < 2);
        bool selfStill = medic.input[0].x == 0 && medic.input[0].y == 0 && medic.bodyChunks[1].ContactPoint.y < 0 && !medic.input[0].thrw && !medic.input[0].jmp;
        bool bodymode = medic.bodyMode == Player.BodyModeIndex.Stand || medic.bodyMode == Player.BodyModeIndex.Crawl;
        return corpseStill && selfStill && bodymode && medic.input[0].pckp;
    }

    private static void RevivePlayer(Player self)
    {
        Data(self).died = true;
        Data(self).death = 0;

        self.Stun(20);
        self.lungsExhausted = true;
        self.exhausted = true;
        self.aerobicLevel = 1;

        self.bodyChunks[0].vel += Custom.RNV() * 2;
        self.bodyChunks[1].vel += Custom.RNV() * 2;

        self.playerState.permanentDamageTracking = 0.5f;
        self.playerState.alive = true;
        self.playerState.permaDead = false;
        self.dead = false;
        self.killTag = null;
        self.killTagCounter = 0;
        self.abstractCreature.abstractAI?.SetDestination(self.abstractCreature.pos);
    }

    public void OnEnable()
    {
        On.RainWorld.Update += RainWorld_Update;
        new Hook(typeof(Player).GetMethod("get_Malnourished"), getMalnourished);
        On.Player.Update += UpdateLife;
        On.Creature.Violence += ReduceLife;
        On.Player.CanEatMeat += DontEatPlayers;
        On.Player.GraphicsModuleUpdated += DontMoveWhileReviving;
        IL.Player.GrabUpdate += Player_GrabUpdate;

        On.PlayerGraphics.Update += PlayerGraphics_Update;
        On.PlayerGraphics.DrawSprites += PlayerGraphics_DrawSprites;
        On.SlugcatHand.Update += SlugcatHand_Update;
    }

    private void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
    {
        try {
            orig(self);
        }
        catch (Exception e) {
            Logger.LogError(e);
        }
    }

    private readonly Func<Func<Player, bool>, Player, bool> getMalnourished = (orig, self) => {
        return orig(self) || Data(self).died;
    };

    private void UpdateLife(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        const int ticksToDie = 40 * 120; // 120 seconds
        const int ticksToRevive = 40 * 7; // 10 seconds

        if (self.dead) {
            ref float death = ref Data(self).death;

            if (death >= 0 && !self.grabbedBy.Any(g => g.grabber is Player p && Data(p).animationTime >= 40)) {
                death += 1f / ticksToDie;
            }
            if (death < 0 && self.dangerGrasp == null) {
                death -= 1f / ticksToRevive;
            }
            if (death < -1) {
                RevivePlayer(self);
                
                if (self.grabbedBy.FirstOrDefault()?.grabber is Player p) {
                    p.ThrowObject(self.grabbedBy[0].graspUsed, eu);
                }
            }
        }
        
        if (Data(self).died) {
            self.slugcatStats.malnourished = true;
            self.slugcatStats.throwingSkill = 0;
        }
    }

    private void ReduceLife(On.Creature.orig_Violence orig, Creature self, BodyChunk source, Vector2? directionAndMomentum, BodyChunk hitChunk, PhysicalObject.Appendage.Pos hitAppendage, Creature.DamageType type, float damage, float stunBonus)
    {
        bool wasDead = self.dead;

        orig(self, source, directionAndMomentum, hitChunk, hitAppendage, type, damage, stunBonus);

        if (self is Player p && wasDead && p.dead && source?.owner is not Lizard && damage > 0) {
            PlayerData data = Data(p);
            if (data.death < 0) {
                data.death = 0;
            }
            if (self.abstractCreature.world.game.clock < data.lastHurt + 3 && damage > data.lastHurtAmount) {
                data.death -= data.lastHurtAmount * 0.15f;
                data.death += damage * 0.15f;
                data.lastHurtAmount = damage;
                Logger.LogDebug($"HURT {p.abstractCreature.ID.number} within cooldown! Damage was {damage}, new death progress is {data.death * 100:0.00} %");
            }
            else {
                data.death += damage * 0.15f;
                Logger.LogDebug($"HURT {p.abstractCreature.ID.number}! Damage was {damage}, new death progress is {data.death * 100:0.00} %");
            }
            data.lastHurt = self.abstractCreature.world.game.clock;
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

        if (Data(self).animationTime > 0) {
            foreach (var grasp in self.grasps) {
                if (grasp?.grabbed is Player p) {
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
        }

        orig(self, actuallyViewed, eu);

        if (pos1 != default) {
            foreach (var grasp in self.grasps) {
                if (grasp?.grabbed is Player p) {
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

    // True to return
    private bool UpdateRevive(Player self, int grasp)
    {
        PlayerData data = Data(self);

        if (self.grasps[grasp]?.grabbed is not Player reviving || !CanRevive(self, reviving)) {
            data.animationTime = 0;
            return false;
        }

        if (self.bodyMode == Player.BodyModeIndex.Crawl) {
            self.standing = true;
        }

        self.animation = ReviveAnimation;
        data.animationTime++;

        Vector2 heartPos = HeartPos(reviving);
        Vector2 targetHeadPos = heartPos + new Vector2(0, Mathf.Sign(self.room.gravity)) * 25;
        Vector2 targetButtPos = heartPos - new Vector2(0, reviving.bodyChunks[0].rad);
        float headDist = (targetHeadPos - self.bodyChunks[0].pos).magnitude;
        float buttDist = (targetButtPos - self.bodyChunks[1].pos).magnitude;

        AnimationStage stage = data.Stage();

        if (stage == AnimationStage.Chill) {
            self.bodyChunks[0].vel += Mathf.Min(headDist, 0.4f) * (targetHeadPos - self.bodyChunks[0].pos).normalized;
            self.bodyChunks[1].vel += Mathf.Min(buttDist, 0.7f) * (targetButtPos - self.bodyChunks[1].pos).normalized;
        }

        if (headDist > 30 || buttDist > 30) {
            data.animationTime = 0;
            return true;
        }

        if (stage is not AnimationStage.Chill and not AnimationStage.CompressionRest and not AnimationStage.MeetingHeads and not AnimationStage.MovingBack) {
            ref float death = ref Data(reviving).death;
            death -= 1 / 40f / 12f;
            if (death > 0) {
                death -= 1 / 40f / 12f; // Revive extra fast if closer to death
            }
        }

        if (!self.playerState.isPup) {
            if (stage is AnimationStage.Chill or AnimationStage.CompressionRest) {
                self.bodyChunkConnections[0].distance = Mathf.Lerp(17, 14, data.animationTime / 40f);
            }
            if (stage is AnimationStage.CompressionDown) {
                self.bodyChunkConnections[0].distance = 10;
            }
            if (stage is AnimationStage.CompressionUp) {
                self.bodyChunkConnections[0].distance = 10f + 4f * (data.animationTime % 20 - 3) / 2f;
            }
        }

        return true;
    }

    private void PlayerGraphics_Update(On.PlayerGraphics.orig_Update orig, PlayerGraphics self)
    {
        orig(self);

        PlayerData data = Data(self.player);

        if (self.malnourished < Mathf.Clamp01(data.death) && self.player.dead) {
            self.malnourished = Mathf.Clamp01(data.death);
        }

        AnimationStage stage = data.Stage();

        if (data.animationTime == 0 || self.player.grasps.FirstOrDefault(g => g.grabbed is Player)?.grabbed is not Player reviving) {
            return;
        }

        Vector2 starePos = data.Compression || stage == AnimationStage.MovingBack
            ? HeartPosCompressed(reviving)
            : reviving.firstChunk.pos + reviving.firstChunk.Rotation * 5;

        self.LookAtPoint(starePos, 10000f);

        if (stage is AnimationStage.CompressionDown) {
            // Push reviving person's head and butt upwards
            PlayerGraphics graf = G(reviving);
            graf.head.vel.y += 0.5f;
            graf.NudgeDrawPosition(0, new(0, 1f));
            if (graf.tail.Length > 1) {
                graf.tail[0].pos.y += 1;
                graf.tail[0].vel.y += 1;
                graf.tail[1].vel.y += 0.5f;
            }
        }
        if (stage is AnimationStage.BreathingOut or AnimationStage.BreathingOutAgain) {
            self.player.Blink(2);
        }
    }

    private void PlayerGraphics_DrawSprites(On.PlayerGraphics.orig_DrawSprites orig, PlayerGraphics self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        orig(self, sLeaser, rCam, timeStacker, camPos);

        if (sLeaser.sprites[9].element.name == "FaceDead" && Data(self.player).death < -0.2f) {
            sLeaser.sprites[9].element = Futile.atlasManager.GetElementWithName("FaceStunned");
        }
    }

    private void SlugcatHand_Update(On.SlugcatHand.orig_Update orig, SlugcatHand self)
    {
        orig(self);

        Player player = ((PlayerGraphics)self.owner).player;
        PlayerData data = Data(player);
        AnimationStage stage = data.Stage();

        if (data.animationTime == 0 || player.grasps.First(g => g.grabbed is Player).grabbed is not Player reviving) {
            return;
        }

        Vector2 offset = new(self.limbNumber == 0 ? -2 : 2, 0);
        Vector2 heart = HeartPos(reviving) + offset;
        Vector2 heartDown = HeartPosCompressed(reviving) + offset;

        if (stage is AnimationStage.Chill or AnimationStage.CompressionRest) {
            self.pos = heart;
        }
        else if (stage == AnimationStage.CompressionDown) {
            self.pos = heartDown;
        }
        else if (stage == AnimationStage.CompressionUp) {
            self.pos = Vector2.Lerp(heartDown, heart, (data.animationTime % 20 - 3) / 2f);
        }
        else if (stage == AnimationStage.BreathingIn) {

        }
    }
}
