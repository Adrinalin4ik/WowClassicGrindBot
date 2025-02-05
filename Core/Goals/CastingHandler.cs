﻿using SharedLib.NpcFinder;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Goals
{
    public class CastingHandler
    {
        private readonly ILogger logger;
        private readonly ConfigurableInput input;

        private readonly Wait wait;
        private readonly AddonReader addonReader;
        private readonly PlayerReader playerReader;
        
        private readonly ClassConfiguration classConfig;
        private readonly IPlayerDirection direction;
        private readonly NpcNameFinder npcNameFinder;
        private readonly StopMoving stopMoving;

        private readonly KeyAction defaultKeyAction = new KeyAction();

        private const int GCD = 1500;
        private const int SpellQueueTimeMs = 400;

        private const int MaxWaitCastTimeMs = GCD;
        private const int MaxWaitBuffTimeMs = GCD;
        private const int MaxCastTimeMs = 15000;
        private const int MaxSwingTimeMs = 4000;
        private const int MaxAirTimeMs = 10000;

        public CastingHandler(ILogger logger, ConfigurableInput input, Wait wait, AddonReader addonReader, ClassConfiguration classConfig, IPlayerDirection direction, NpcNameFinder npcNameFinder, StopMoving stopMoving)
        {
            this.logger = logger;
            this.input = input;

            this.wait = wait;
            this.addonReader = addonReader;
            this.playerReader = addonReader.PlayerReader;
            
            this.classConfig = classConfig;
            this.direction = direction;
            this.npcNameFinder = npcNameFinder;
            this.stopMoving = stopMoving;
        }

        protected bool CanRun(KeyAction item)
        {
            if (!string.IsNullOrEmpty(item.CastIfAddsVisible))
            {
                var needAdds = bool.Parse(item.CastIfAddsVisible);
                if (needAdds != npcNameFinder.PotentialAddsExist)
                {
                    item.LogInformation($"Only cast if adds exist = {item.CastIfAddsVisible} and it is {npcNameFinder.PotentialAddsExist} - Targets:{npcNameFinder.TargetCount} - Adds:{npcNameFinder.AddCount}");
                    return false;
                }
                else
                {
                    item.LogInformation($"Only cast if adds exist = {item.CastIfAddsVisible} and it is {npcNameFinder.PotentialAddsExist} - Targets:{npcNameFinder.TargetCount} - Adds:{npcNameFinder.AddCount}");
                }
            }

            if(item.School != SchoolMask.None)
            {
                if(classConfig.ImmunityBlacklist.TryGetValue(playerReader.TargetId, out var list))
                {
                    if(list.Contains(item.School))
                    {
                        return false;
                    }
                }
            }

            return item.CanRun();
        }

        protected async Task PressCastKeyAndWaitForCastToEnd(ConsoleKey key, int maxWaitMs)
        {
            await PressKey(key);
            if (!this.playerReader.IsCasting)
            {
                // try again
                await PressKey(key);
            }

            for (int i = 0; i < maxWaitMs; i += 100)
            {
                if (!this.playerReader.IsCasting)
                {
                    return;
                }
                await Task.Delay(100);
            }
        }

        private async Task PressKeyAction(KeyAction item)
        {
            playerReader.LastUIErrorMessage = UI_ERROR.NONE;

            await PressKey(item.ConsoleKey, item.Log ? item.Name + (item.AfterCastWaitNextSwing ? " and wait for next swing!" : "") : string.Empty, item.PressDuration);
            item.SetClicked();
        }

        private static bool CastSuccessfull(UI_ERROR uiEvent)
        {
            return
                uiEvent == UI_ERROR.CAST_START ||
                uiEvent == UI_ERROR.CAST_SUCCESS ||
                uiEvent == UI_ERROR.NONE;
        }

        private async Task<bool> CastInstant(KeyAction item)
        {
            if (item.StopBeforeCast)
            {
                await stopMoving.Stop();
                await wait.Update(1);
            }

            playerReader.CastEvent.ForceUpdate(0);
            int beforeCastEventValue = playerReader.CastEvent.Value;
            int beforeSpellId = playerReader.CastSpellId.Value;
            bool beforeUsable = addonReader.UsableAction.Is(item);

            await PressKeyAction(item);

            bool inputNotHappened;
            double inputElapsedMs;

            if (item.AfterCastWaitNextSwing)
            {
                (inputNotHappened, inputElapsedMs) = await wait.InterruptTask(MaxSwingTimeMs,
                    interrupt: () => !addonReader.CurrentAction.Is(item),
                    repeat: async () =>
                    {
                        if (classConfig.Approach.GetCooldownRemaining() == 0)
                        {
                            await input.TapApproachKey("");
                        }
                    });
            }
            else
            {
                (inputNotHappened, inputElapsedMs) = await wait.InterruptTask(MaxWaitCastTimeMs,
                    interrupt: () =>
                    (beforeSpellId != playerReader.CastSpellId.Value && beforeCastEventValue != playerReader.CastEvent.Value) ||
                    beforeUsable != addonReader.UsableAction.Is(item)
                );
            }

            if (!inputNotHappened)
            {
                item.LogInformation($" ... instant input {inputElapsedMs}ms");
            }
            else
            {
                item.LogInformation($" ... instant input not registered! {inputElapsedMs}ms");
                return false;
            }

            item.LogInformation($" ... usable: {beforeUsable}->{addonReader.UsableAction.Is(item)} -- ({(UI_ERROR)beforeCastEventValue}->{(UI_ERROR)playerReader.CastEvent.Value})");

            if (!CastSuccessfull((UI_ERROR)playerReader.CastEvent.Value))
            {
                await ReactToLastCastingEvent(item, $"{item.Name}-{GetType().Name}: CastInstant");
                return false;
            }
            return true;
        }

        private async Task<bool> CastCastbar(KeyAction item)
        {
            if (playerReader.Bits.IsFalling)
            {
                (bool notfalling, double fallingElapsedMs) = await wait.InterruptTask(MaxAirTimeMs, () => !playerReader.Bits.IsFalling);
                if (!notfalling)
                {
                    item.LogInformation($" ... castbar waited for landing {fallingElapsedMs}ms");
                }
            }

            await stopMoving.Stop();
            await wait.Update(1);

            bool beforeHasTarget = playerReader.HasTarget;

            bool beforeUsable = addonReader.UsableAction.Is(item);
            int beforeCastEventValue = playerReader.CastEvent.Value;
            int beforeSpellId = playerReader.CastSpellId.Value;
            int beforeCastCount = playerReader.CastCount;

            await PressKeyAction(item);

            (bool input, double inputElapsedMs) = await wait.InterruptTask(MaxWaitCastTimeMs,
                interrupt: () =>
                beforeCastEventValue != playerReader.CastEvent.Value ||
                beforeSpellId != playerReader.CastSpellId.Value ||
                beforeCastCount != playerReader.CastCount
                );

            if (!input)
            {
                item.LogInformation($" ... castbar input {inputElapsedMs}ms");
            }
            else
            {
                item.LogInformation($" ... castbar input not registered! {inputElapsedMs}ms");
                return false;
            }

            item.LogInformation($" ... casting: {playerReader.IsCasting} -- count:{playerReader.CastCount} -- usable: {beforeUsable}->{addonReader.UsableAction.Is(item)} -- {(UI_ERROR)beforeCastEventValue}->{(UI_ERROR)playerReader.CastEvent.Value}");

            if (!CastSuccessfull((UI_ERROR)playerReader.CastEvent.Value))
            {
                await ReactToLastCastingEvent(item, $"{item.Name}-{GetType().Name}: CastCastbar");
                return false;
            }

            if (playerReader.IsCasting)
            {
                item.LogInformation(" ... waiting for visible cast bar to end or target loss.");
                await wait.InterruptTask(MaxCastTimeMs, () => !playerReader.IsCasting || beforeHasTarget != playerReader.HasTarget);
            }
            else if ((UI_ERROR)playerReader.CastEvent.Value == UI_ERROR.CAST_START)
            {
                beforeCastEventValue = playerReader.CastEvent.Value;
                item.LogInformation(" ... waiting for hidden cast bar to end or target loss.");
                await wait.InterruptTask(MaxCastTimeMs, () => beforeCastEventValue != playerReader.CastEvent.Value || beforeHasTarget != playerReader.HasTarget);
            }

            return true;
        }

        public async Task<bool> CastIfReady(KeyAction item, int sleepBeforeCast = 0)
        {
            if (!CanRun(item))
            {
                return false;
            }

            if (item.ConsoleKey == 0)
            {
                return false;
            }

            if (item.Name == classConfig.Approach.Name ||
                item.Name == classConfig.AutoAttack.Name ||
                item.Name == classConfig.Interact.Name)
            {
                await PressKeyAction(item);
                return true;
            }

            bool beforeUsable = addonReader.UsableAction.Is(item);
            var beforeForm = playerReader.Form;

            if (!await SwitchToCorrectStanceForm(beforeForm, item))
            {
                return false;
            }

            if (beforeForm != playerReader.Form && !beforeUsable && !addonReader.UsableAction.Is(item))
            {
                item.LogInformation(" ... after Form switch still not usable!");
                return false;
            }

            if (playerReader.Bits.IsAutoRepeatSpellOn_Shoot)
            {
                await input.TapStopAttack("Stop AutoRepeat Shoot");
                await input.TapStopAttack("Stop AutoRepeat Shoot");
                await wait.Update(1);

                (bool interrupted, double elapsedMs) = await wait.InterruptTask(GCD, 
                    () => addonReader.UsableAction.Is(item));

                if (!interrupted)
                {
                    item.LogInformation($" ... waited to end Shoot {elapsedMs}ms");
                }
            }

            if (sleepBeforeCast > 0)
            {
                if (item.StopBeforeCast || item.HasCastBar)
                {
                    await stopMoving.Stop();
                    await wait.Update(1);
                    await stopMoving.Stop();
                    await wait.Update(1);
                }

                item.LogInformation($" Wait {sleepBeforeCast}ms before press.");
                await Task.Delay(sleepBeforeCast);
            }

            bool beforeHasTarget = playerReader.HasTarget;
            int auraHash = playerReader.AuraCount.Hash;


            if (!await WaitForGCD(item, beforeHasTarget))
            {
                return false;
            }

            if (!item.HasCastBar)
            {
                if (!await CastInstant(item))
                {
                    // try again after reacted to UI_ERROR
                    if (!await CastInstant(item))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (!await CastCastbar(item))
                {
                    // try again after reacted to UI_ERROR
                    if (!await CastCastbar(item))
                    {
                        return false;
                    }
                }
            }

            if (item.AfterCastWaitBuff)
            {
                (bool notappeared, double elapsedMs) = await wait.InterruptTask(MaxWaitBuffTimeMs, () => auraHash != playerReader.AuraCount.Hash);
                item.LogInformation($" ... AfterCastWaitBuff: Buff: {!notappeared} | pb: {playerReader.AuraCount.PlayerBuff} | pd: {playerReader.AuraCount.PlayerDebuff} | tb: {playerReader.AuraCount.TargetBuff} | td: {playerReader.AuraCount.TargetDebuff} | Delay: {elapsedMs}ms");
            }

            if (item.DelayAfterCast != defaultKeyAction.DelayAfterCast)
            {
                if (item.DelayUntilCombat) // stop waiting if the mob is targetting me
                {
                    item.LogInformation($" ... DelayUntilCombat ... delay after cast {item.DelayAfterCast}ms");

                    var sw = new Stopwatch();
                    sw.Start();
                    while (sw.ElapsedMilliseconds < item.DelayAfterCast)
                    {
                        await wait.Update(1);
                        if (playerReader.Bits.TargetOfTargetIsPlayer)
                        {
                            break;
                        }
                    }
                }
                else if (item.DelayAfterCast > 0)
                {
                    item.LogInformation($" ... delay after cast {item.DelayAfterCast}ms");
                    var result = await wait.InterruptTask(item.DelayAfterCast, () => beforeHasTarget != playerReader.HasTarget);
                    if (!result.Item1)
                    {
                        item.LogInformation($" .... delay after cast interrupted, target changed {result.Item2}ms");
                    }
                    else
                    {
                        item.LogInformation($" .... delay after cast not interrupted {result.Item2}ms");
                    }
                }
            }
            else
            {
                if (item.RequirementObjects.Any())
                {
                    (bool firstReq, double firstReqElapsedMs) = await wait.InterruptTask(SpellQueueTimeMs,
                        () => !item.CanRun()
                    );
                    item.LogInformation($" ... instant interrupt: {!firstReq} | CanRun:{item.CanRun()} | Delay: {firstReqElapsedMs}ms");
                }
            }

            if (item.StepBackAfterCast > 0)
            {
                input.SetKeyState(ConsoleKey.DownArrow, true, false, $"Step back for {item.StepBackAfterCast}ms");
                (bool notStepback, double stepbackElapsedMs) =
                    await wait.InterruptTask(item.StepBackAfterCast, () => beforeHasTarget != playerReader.HasTarget);
                if (!notStepback)
                {
                    item.LogInformation($" .... interrupted stepback | lost target? {beforeHasTarget != playerReader.HasTarget} | {stepbackElapsedMs}ms");
                }
                input.SetKeyState(ConsoleKey.DownArrow, false, false);
            }

            if (item.AfterCastWaitNextSwing)
            {
                await wait.Update(1);
            }

            item.ConsumeCharge();
            return true;
        }

        private async Task<bool> WaitForGCD(KeyAction item, bool beforeHasTarget)
        {
            if (item.WaitForGCD)
            {
                (bool gcd, double gcdElapsedMs) = await wait.InterruptTask(GCD,
                    () => addonReader.UsableAction.Is(item) || beforeHasTarget != playerReader.HasTarget);
                if (!gcd)
                {
                    item.LogInformation($" ... gcd interrupted {gcdElapsedMs}ms");

                    if (beforeHasTarget != playerReader.HasTarget)
                    {
                        item.LogInformation($" ... lost target!");
                        return false;
                    }
                }
                else
                {
                    item.LogInformation($" ... gcd fully waited {gcdElapsedMs}ms");
                }
            }

            return true;
        }

        protected async Task<bool> SwitchToCorrectStanceForm(Form beforeForm, KeyAction item)
        {
            if (string.IsNullOrEmpty(item.Form))
                return true;

            if (playerReader.Form == item.FormEnum)
            {
                return true;
            }

            var formKeyAction = classConfig.Form
                .Where(s => s.FormEnum == item.FormEnum)
                .FirstOrDefault();

            if (formKeyAction == null)
            {
                logger.LogWarning($"Unable to find key in Form to transform into {item.FormEnum}");
                return false;
            }

            await input.KeyPress(formKeyAction.ConsoleKey, formKeyAction.PressDuration);
            (bool notChanged, double elapsedMs) = await wait.InterruptTask(SpellQueueTimeMs, () => beforeForm != playerReader.Form);
            item.LogInformation($" ... form changed: {!notChanged} | Delay: {elapsedMs}ms");

            if (playerReader.Form == Form.None)
            {
                item.LogInformation($" ... wait for GCD after form change {beforeForm}->{playerReader.Form}!");
                await WaitForGCD(item, playerReader.HasTarget);
            }

            return playerReader.Form == item.FormEnum;
        }

        public async Task PressKey(ConsoleKey key, string description = "", int duration = 50)
        {
            await input.KeyPress(key, duration, description);
        }

        public async Task ReactToLastUIErrorMessage(string source)
        {
            //var lastError = playerReader.LastUIErrorMessage;
            switch (playerReader.LastUIErrorMessage)
            {
                case UI_ERROR.NONE:
                    break;
                case UI_ERROR.CAST_START:
                    break;
                case UI_ERROR.CAST_SUCCESS:
                    break;
                case UI_ERROR.ERR_SPELL_FAILED_STUNNED:
                    int debuffCount = playerReader.AuraCount.PlayerDebuff;
                    if (debuffCount != 0)
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_FAILED_STUNNED} -- Wait till losing debuff!");
                        await wait.While(() => debuffCount == playerReader.AuraCount.PlayerDebuff);

                        await wait.Update(1);
                        playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    }
                    else
                    {
                        logger.LogInformation($"{source} -- Didn't know how to react {UI_ERROR.ERR_SPELL_FAILED_STUNNED} when PlayerDebuffCount: {debuffCount}");
                    }
                    break;
                case UI_ERROR.ERR_SPELL_OUT_OF_RANGE:
                    if (playerReader.Class == PlayerClassEnum.Hunter && playerReader.IsInMeleeRange)
                    {
                        logger.LogInformation($"{source} -- As a Hunter didn't know how to react {UI_ERROR.ERR_SPELL_OUT_OF_RANGE}");
                        return;
                    }

                    logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_OUT_OF_RANGE} -- Face enemy and start moving forward");
                    await input.TapInteractKey("");
                    input.SetKeyState(ConsoleKey.UpArrow, true, false, "");

                    await wait.Update(1);
                    playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    break;
                case UI_ERROR.ERR_BADATTACKFACING:

                    if (playerReader.IsInMeleeRange)
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_BADATTACKFACING} -- Interact!");
                        await input.TapInteractKey("");
                    }
                    else
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_BADATTACKFACING} -- Turning 180!");

                        double desiredDirection = playerReader.Direction + Math.PI;
                        desiredDirection = desiredDirection > Math.PI * 2 ? desiredDirection - (Math.PI * 2) : desiredDirection;
                        await direction.SetDirection(desiredDirection, new WowPoint(0, 0), "");
                    }

                    await wait.Update(1);
                    playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    break;
                case UI_ERROR.SPELL_FAILED_MOVING:
                    logger.LogInformation($"{source} -- React to {UI_ERROR.SPELL_FAILED_MOVING} -- Stop moving!");

                    await stopMoving.Stop();
                    await wait.Update(1);
                    playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    break;
                case UI_ERROR.ERR_SPELL_FAILED_ANOTHER_IN_PROGRESS:
                    logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_FAILED_ANOTHER_IN_PROGRESS} -- Wait till casting!");
                    await wait.While(() => playerReader.IsCasting);

                    await wait.Update(1);
                    playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    break;
                case UI_ERROR.ERR_SPELL_COOLDOWN:
                    logger.LogInformation($"{source} -- Cant react to {UI_ERROR.ERR_SPELL_FAILED_ANOTHER_IN_PROGRESS}");

                    await wait.Update(1);
                    playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    break;
                case UI_ERROR.ERR_BADATTACKPOS:
                    if (playerReader.Bits.IsAutoRepeatSpellOn_AutoAttack)
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_BADATTACKPOS} -- Interact!");
                        await input.TapInteractKey("");
                        await stopMoving.Stop();
                        await wait.Update(1);

                        playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    }
                    else
                    {
                        logger.LogInformation($"{source} -- Didn't know how to React to {playerReader.LastUIErrorMessage}");
                    }
                    break;
                default:
                    logger.LogInformation($"{source} -- Didn't know how to React to {playerReader.LastUIErrorMessage}");
                    break;
                //case UI_ERROR.ERR_SPELL_FAILED_S:
                //case UI_ERROR.ERR_BADATTACKPOS:
                //case UI_ERROR.ERR_SPELL_OUT_OF_RANGE:
                //case UI_ERROR.ERR_AUTOFOLLOW_TOO_FAR:
                //    this.playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                //    break;
            }
        }

        private async Task ReactToLastCastingEvent(KeyAction item, string source)
        {
            switch ((UI_ERROR)playerReader.CastEvent.Value)
            {
                case UI_ERROR.NONE:
                    break;
                case UI_ERROR.ERR_SPELL_FAILED_INTERRUPTED:
                    item.SetClicked();
                    break;
                case UI_ERROR.CAST_START:
                    break;
                case UI_ERROR.CAST_SUCCESS:
                    break;
                case UI_ERROR.ERR_SPELL_COOLDOWN:
                    logger.LogInformation($"{source} React to {UI_ERROR.ERR_SPELL_COOLDOWN} -- wait until its ready");
                    bool before = addonReader.UsableAction.Is(item);
                    await wait.While(() => before != addonReader.UsableAction.Is(item));

                    break;
                case UI_ERROR.ERR_SPELL_FAILED_STUNNED:
                    int debuffCount = playerReader.AuraCount.PlayerDebuff;
                    if (debuffCount != 0)
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_FAILED_STUNNED} -- Wait till losing debuff!");
                        await wait.While(() => debuffCount == playerReader.AuraCount.PlayerDebuff);
                    }
                    else
                    {
                        logger.LogInformation($"{source} -- Didn't know how to react {UI_ERROR.ERR_SPELL_FAILED_STUNNED} when PlayerDebuffCount: {debuffCount}");
                    }

                    break;
                case UI_ERROR.ERR_SPELL_OUT_OF_RANGE:
                    if (playerReader.Class == PlayerClassEnum.Hunter && playerReader.IsInMeleeRange)
                    {
                        logger.LogInformation($"{source} -- As a Hunter didn't know how to react {UI_ERROR.ERR_SPELL_OUT_OF_RANGE}");
                        return;
                    }

                    float minRange = playerReader.MinRange;
                    if (playerReader.Bits.PlayerInCombat && playerReader.HasTarget && !playerReader.IsTargetCasting)
                    {
                        await wait.Update(2);
                        if (playerReader.TargetTarget == TargetTargetEnum.TargetIsTargettingMe)
                        {
                            logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_OUT_OF_RANGE} -- Just wait for the target to get in range.");
                            
                            (bool inputNotHappened, double inputElapsedMs) = await wait.InterruptTask(MaxWaitCastTimeMs,
                                () => minRange != playerReader.MinRange || playerReader.IsTargetCasting
                            );
                        }
                    }
                    else
                    {
                        double beforeDirection = playerReader.Direction;
                        await input.TapInteractKey("");
                        await input.TapStopAttack();
                        await stopMoving.Stop();
                        await wait.Update(1);

                        if (beforeDirection != playerReader.Direction)
                        {
                            await input.TapInteractKey("");

                            (bool inputNotHappened, double inputElapsedMs) = await wait.InterruptTask(MaxWaitCastTimeMs,
                                () => minRange != playerReader.MinRange);

                            logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_OUT_OF_RANGE} -- Approached target {minRange}->{playerReader.MinRange}");
                        }
                        else
                        {
                            logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_OUT_OF_RANGE} -- Start moving forward");
                            input.SetKeyState(ConsoleKey.UpArrow, true, false, "");
                        }


                    }

                    break;
                case UI_ERROR.ERR_BADATTACKFACING:
                    if (playerReader.IsInMeleeRange)
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_BADATTACKFACING} -- Interact!");
                        await input.TapInteractKey("");
                    }
                    else
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_BADATTACKFACING} -- Turning 180!");

                        double desiredDirection = playerReader.Direction + Math.PI;
                        desiredDirection = desiredDirection > Math.PI * 2 ? desiredDirection - (Math.PI * 2) : desiredDirection;
                        await direction.SetDirection(desiredDirection, new WowPoint(0, 0), "");

                        await wait.Update(1);
                    }

                    break;
                case UI_ERROR.SPELL_FAILED_MOVING:
                    logger.LogInformation($"{source} -- React to {UI_ERROR.SPELL_FAILED_MOVING} -- Stop moving!");
                    await stopMoving.Stop();
                    await wait.Update(1);

                    break;
                case UI_ERROR.ERR_SPELL_FAILED_ANOTHER_IN_PROGRESS:
                    logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_SPELL_FAILED_ANOTHER_IN_PROGRESS} -- Wait till casting!");
                    await wait.While(() => playerReader.IsCasting);

                    break;
                case UI_ERROR.ERR_BADATTACKPOS:
                    if (playerReader.Bits.IsAutoRepeatSpellOn_AutoAttack)
                    {
                        logger.LogInformation($"{source} -- React to {UI_ERROR.ERR_BADATTACKPOS} -- Interact!");
                        await input.TapInteractKey("");
                        await stopMoving.Stop();
                        await wait.Update(1);
                    }
                    else
                    {
                        logger.LogInformation($"{source} -- Didn't know how to React to {(UI_ERROR)playerReader.CastEvent.Value}");
                    }

                    break;
                default:
                    logger.LogInformation($"{source} -- Didn't know how to React to {(UI_ERROR)playerReader.CastEvent.Value}");

                    break;
                    //case UI_ERROR.ERR_SPELL_FAILED_S:
                    //case UI_ERROR.ERR_BADATTACKPOS:
                    //case UI_ERROR.ERR_SPELL_OUT_OF_RANGE:
                    //case UI_ERROR.ERR_AUTOFOLLOW_TOO_FAR:
                    //    this.playerReader.LastUIErrorMessage = UI_ERROR.NONE;
                    //    break;
            }
        }
    }
}