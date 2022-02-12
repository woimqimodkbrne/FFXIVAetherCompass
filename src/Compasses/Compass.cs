﻿using AetherCompass.Common;
using AetherCompass.Common.Attributes;
using AetherCompass.Compasses.Objectives;
using AetherCompass.Configs;
using AetherCompass.Game;
using AetherCompass.UI;
using AetherCompass.UI.GUI;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ObjectInfo = FFXIVClientStructs.FFXIV.Client.UI.UI3DModule.ObjectInfo;
using ImGuiNET;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;



namespace AetherCompass.Compasses
{
    public abstract class Compass
    {
        private bool ready = false;

        // Record last and 2nd last closest to prevent frequent notification when player is at a pos close to two objs
        private CachedCompassObjective? closestObj;
        private IntPtr closestObjPtrLast;
        private IntPtr closestObjPtrSecondLast;
        private DateTime closestObjLastChangedTime = DateTime.MinValue;
        private const int closestObjResetDelayInSec = 60;
        

        public abstract string CompassName { get; }
        public abstract string Description { get; }

        private CompassType _compassType = CompassType.Unknown;
        public CompassType CompassType
        {
            get
            {
                if (_compassType == CompassType.Unknown)
                    _compassType = (GetType().GetCustomAttributes(typeof(CompassTypeAttribute), false)[0] as CompassTypeAttribute)?
                        .Type ?? CompassType.Invalid;
                return _compassType;
            }
        }
        

        private bool _compassEnabled = false;
        public bool CompassEnabled
        {
            get => _compassEnabled;
            set 
            {
                if (!value) DisposeCompassUsedIcons();
                _compassEnabled = value;
            }
        }

        private protected abstract CompassConfig CompassConfig { get; }

        public virtual bool MarkScreen => Plugin.Config.ShowScreenMark && CompassConfig.MarkScreen;
        public virtual bool ShowDetail => Plugin.Config.ShowDetailWindow && CompassConfig.ShowDetail;

        public virtual bool NotifyChat => Plugin.Config.NotifyChat && CompassConfig.NotifyChat;
        public virtual bool NotifySe => Plugin.Config.NotifySe && CompassConfig.NotifySe;
        public virtual bool NotifyToast => Plugin.Config.NotifyToast && CompassConfig.NotifyToast;


        public Compass()
        {
            _compassEnabled = CompassConfig.Enabled;   // assign to field to avoid trigger Icon manager when init
            ready = true;
        }


        public abstract bool IsEnabledInCurrentTerritory();
        public unsafe abstract bool IsObjective(GameObject* o);
        private protected unsafe abstract string GetClosestObjectiveDescription(CachedCompassObjective objective);
        public unsafe abstract DrawAction? CreateDrawDetailsAction(CachedCompassObjective objective);
        public unsafe abstract DrawAction? CreateMarkScreenAction(CachedCompassObjective objective);

        private protected abstract void DisposeCompassUsedIcons();

        protected unsafe virtual CachedCompassObjective CreateCompassObjective(GameObject* obj)
            => new(obj);

        public unsafe virtual void UpdateClosestObjective(CachedCompassObjective objective)
        {
            if (closestObj == null) closestObj = objective;
            else if (objective.Distance3D < closestObj.Distance3D)
                closestObj = objective;
        }

        public virtual void ProcessOnLoopStart()
        { }

        public virtual void ProcessOnLoopEnd()
        {
            ProcessClosestObjOnLoopEnd();
        }

        public unsafe void ProcessLoop(ObjectInfo** infoArray, int count, CancellationToken token)
        {
            Task.Run(() =>
            {
                ProcessOnLoopStart();
                for (int i = 0; i < count; i++)
                {
                    if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                    var info = infoArray[i];
                    var obj = info != null ? info->GameObject : null;
                    if (obj == null || obj->ObjectKind == (byte)ObjectKind.Pc) continue;
                    if (!IsObjective(obj)) continue;
                    var objective = CreateCompassObjective(obj);
                    ProcessObjectiveInLoop(objective);
                }
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                ProcessOnLoopEnd();
            }, token).ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    foreach (var e in t.Exception.InnerExceptions)
                    {
                        if (e is OperationCanceledException or ObjectDisposedException) continue;
                        Plugin.LogError(e.ToString());
                    }
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

#if DEBUG
        public unsafe void ProcessLoopDebugAllObjects(GameObject** GameObjectList, int count, CancellationToken token)
        {
            Task.Run(() =>
            {
                ProcessOnLoopStart();
                for (int i = 0; i < count; i++)
                {
                    if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                    var obj = GameObjectList[i];
                    if (obj == null) continue;
                    if (!IsObjective(obj)) continue;
                    var objective = CreateCompassObjective(obj);
                    ProcessObjectiveInLoop(objective);
                }
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                ProcessOnLoopEnd();
            }, token).ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    foreach (var e in t.Exception.InnerExceptions)
                    {
                        if (e is OperationCanceledException or ObjectDisposedException) continue;
                        Plugin.LogError(e.ToString());
                    }
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
#endif

        private void ProcessObjectiveInLoop(CachedCompassObjective objective)
        {
            UpdateClosestObjective(objective);

            if (ShowDetail)
            {
                var action = CreateDrawDetailsAction(objective);
                Plugin.DetailsWindow.AddDrawAction(this, action);
            }
            if (MarkScreen)
            {
                var action = CreateMarkScreenAction(objective);
                Plugin.Overlay.AddDrawAction(action);
            }
        }

        private void ProcessObjectiveInLoop(CachedCompassObjective objective, CancellationToken token)
        {
            try
            {
                Task.Run(() =>
                {
                    ProcessObjectiveInLoop(objective);
                }, token).ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        foreach (var e in t.Exception.InnerExceptions)
                            Plugin.LogError(e.ToString());
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (AggregateException e) 
            { 
                if (e.InnerException is not (TaskCanceledException or ObjectDisposedException))
                    throw;
            }
        }

        private unsafe void ProcessClosestObjOnLoopEnd()
        {
            if (ready)
            {
                if ((DateTime.UtcNow - closestObjLastChangedTime).TotalSeconds > closestObjResetDelayInSec)
                {
                    closestObjPtrSecondLast = IntPtr.Zero;
                    closestObjLastChangedTime = DateTime.UtcNow;
                }
                else if (closestObj != null && !closestObj.IsEmpty()
                    && !closestObj.IsCacheFor(closestObjPtrLast)
                    && !closestObj.IsCacheFor(closestObjPtrSecondLast))
                {
                    if (NotifyChat)
                    {
                        var msg = Chat.CreateMapLink(
                            Plugin.ClientState.TerritoryType, ZoneWatcher.CurrentMapId,
                            closestObj.CurrentMapCoord, CompassUtil.CurrentHasZCoord());
                        msg.PrependText($"Found {GetClosestObjectiveDescription(closestObj)} at ");
                        msg.AppendText($", on {closestObj.CompassDirectionFromPlayer}, " +
                            $"{CompassUtil.DistanceToDescriptiveString(closestObj.Distance3D, false)} from you");
                        Notifier.TryNotifyByChat(msg, NotifySe, CompassConfig.NotifySeId);
                    }
                    if (NotifyToast)
                    {
                        var msg =
                            $"Found {GetClosestObjectiveDescription(closestObj)} " +
                            $"on {closestObj.CompassDirectionFromPlayer}, " +
                            $"{CompassUtil.DistanceToDescriptiveString(closestObj.Distance3D, true)} from you, " +
                            $"at {CompassUtil.MapCoordToFormattedString(closestObj.CurrentMapCoord)}";
                        Notifier.TryNotifyByToast(msg);
                    }
                    closestObjPtrSecondLast = closestObjPtrLast;
                    closestObjPtrLast = closestObj.GameObject;
                    closestObjLastChangedTime = DateTime.UtcNow;
                }
            }
        }

        public virtual void Reset()
        {
            closestObj = null;
        }


        public async virtual void OnZoneChange()
        {
            ready = false;
            Reset();
            closestObjPtrLast = IntPtr.Zero;
            closestObjPtrSecondLast = IntPtr.Zero;
            await Task.Delay(2500);
            ready = true;
        }


        #region Config UI
        public void DrawConfigUi()
        {
            var name = CompassType is CompassType.Experimental or CompassType.Debug
                ? $"[{CompassType}] ".ToUpper() + CompassName : CompassName;
            ImGuiEx.Checkbox(name, ref CompassConfig.Enabled);
            // Also dispose icons if disabled
            if (CompassConfig.Enabled != _compassEnabled) CompassEnabled = CompassConfig.Enabled;
            ImGui.Indent();
            ImGuiEx.IconTextCompass(nextSameLine: true);
            ImGui.TextWrapped(Description);
            if (CompassType == CompassType.Experimental)
                ImGui.TextDisabled("Experimental compasses may not work as expected.\nPlease enable with caution.");
            ImGui.Unindent();
            if (CompassConfig.Enabled)
            {
                ImGui.PushID($"{CompassName}");
                if (ImGui.TreeNode($"Compass settings"))
                {
                    ImGui.BulletText("UI:");
                    ImGui.Indent();
                    if (Plugin.Config.ShowScreenMark)
                        ImGuiEx.Checkbox("Mark detected objects on screen", ref CompassConfig.MarkScreen,
                            "Mark objects detected by this compass on screen, showing the direction and distance.");
                    else ImGui.TextDisabled("Mark-on-screen disabled in Plugin Settings");
                    if (Plugin.Config.ShowDetailWindow)
                        ImGuiEx.Checkbox("Show objects details", ref CompassConfig.ShowDetail,
                            "List details of objects detected by this compass in the Details Window.");
                    else ImGui.TextDisabled("Detail Window disabled in Plugin Settings");
                    ImGui.Unindent();

                    ImGui.BulletText("Notifications:");
                    ImGui.Indent();
                    if (Plugin.Config.NotifyChat)
                    {
                        ImGuiEx.Checkbox("Chat", ref CompassConfig.NotifyChat,
                            "Allow this compass to send a chat message about an object detected.");
                        if (Plugin.Config.NotifySe)
                        {
                            ImGuiEx.Checkbox("Sound", ref CompassConfig.NotifySe,
                                "Also allow this compass to make sound when sending chat message notification.");
                            if (CompassConfig.NotifySe)
                            {
                                ImGui.Indent();
                                ImGuiEx.InputInt("Sound Effect ID", 100, ref CompassConfig.NotifySeId,
                                    "Input the Sound Effect ID for sound notification, from 1 to 16.\n\n" +
                                    "Sound Effect ID is the same as the game's macro sound effects <se.1>~<se.16>. " +
                                    "For example, if <se.1> is to be used, then enter \"1\" here.");
                                if (CompassConfig.NotifySeId < 1) CompassConfig.NotifySeId = 1;
                                if (CompassConfig.NotifySeId > 16) CompassConfig.NotifySeId = 16;
                                ImGui.Unindent();
                            }
                        }
                        else ImGui.TextDisabled("Sound notification disabled in Plugin Settings");
                    }
                    else ImGui.TextDisabled("Chat notification disabled in Plugin Settings");
                    if (Plugin.Config.NotifyToast)
                    {
                        ImGuiEx.Checkbox("Toast", ref CompassConfig.NotifyToast,
                            "Allow this compass to make a Toast notification about an object detected.");
                    }
                    else ImGui.TextDisabled("Toast notification disabled in Plugin Settings");
                    ImGui.Unindent();

                    DrawConfigUiExtra();
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
        }

        public virtual void DrawConfigUiExtra() { }
        #endregion


        #region Helpers

        protected void DrawFlagButton(string id, Vector3 mapCoordToFlag)
        {
            if (ImGui.Button($"Set flag on map##{CompassName}_{id}"))
                Plugin.CompassManager.RegisterMapFlag(new(mapCoordToFlag.X, mapCoordToFlag.Y));
        }

        internal static DrawAction? GenerateConfigDummyMarkerDrawAction(string info, float scale)
        {
            var icon = IconManager.ConfigDummyMarkerIcon;
            if (icon == null) info = "(Failed to load icon)\n" + info;
            var drawPos = UiHelper.GetScreenCentre();
            return DrawAction.Combine(important: true,
                GenerateScreenMarkerIconDrawAction(icon, drawPos, IconManager.MarkerIconSize, scale, 1, out drawPos),
                GenerateExtraInfoDrawAction(info, scale, new(1, 1, 1, 1), 0, drawPos, IconManager.MarkerIconSize, 0, out _));
        }

        protected static readonly Vector2 BaseMarkerSize 
            = IconManager.MarkerIconSize + IconManager.DirectionScreenIndicatorIconSize;
        
        protected static DrawAction? GenerateDefaultScreenMarkerDrawAction(CachedCompassObjective obj,
            ImGuiScene.TextureWrap? icon, Vector2 iconSizeRaw, float iconAlpha, string info,
            Vector4 infoTextColour, float textShadowLightness, out Vector2 lastDrawEndPos, bool important = false)
        {
            Vector3 hitboxPosAdjusted = new(obj.Position.X, obj.Position.Y + obj.GameObjectHeight + .5f, obj.Position.Z);
            bool inFrontOfCamera = UiHelper.WorldToScreenPos(hitboxPosAdjusted, out var screenPos);
            screenPos = PushToSideOnXIfNeeded(screenPos, inFrontOfCamera);
            bool insideMainViewport = UiHelper.IsScreenPosInsideMainViewport(screenPos);
            float rotationFromUpward = UiHelper.GetAngleOnScreen(screenPos);

            var scaledBaseMarkerSize = BaseMarkerSize * Plugin.Config.ScreenMarkSizeScale;

            lastDrawEndPos = UiHelper.GetConstrainedScreenPos(screenPos, Plugin.Config.ScreenMarkConstraint, scaledBaseMarkerSize / 4);

            if (!insideMainViewport)
                rotationFromUpward = -rotationFromUpward;
            else
            {
                // Flip the direction indicator when the indicator originally points towards centre
                // but need to be flipped due to the screen constraint pushing the whole marker inwards
                if (lastDrawEndPos.X > screenPos.X && rotationFromUpward < 0
                    || lastDrawEndPos.X < screenPos.X && rotationFromUpward > 0)
                    rotationFromUpward = MathUtil.PI2 - rotationFromUpward;
                if (lastDrawEndPos.Y > screenPos.Y && Math.Abs(rotationFromUpward) > MathUtil.PIOver2
                    || lastDrawEndPos.Y < screenPos.Y && MathF.Abs(rotationFromUpward) < MathUtil.PIOver2)
                    rotationFromUpward = MathF.PI - rotationFromUpward;
                if (rotationFromUpward > MathF.PI) rotationFromUpward -= MathUtil.PI2;
                if (rotationFromUpward < -MathF.PI) rotationFromUpward += MathUtil.PI2;
            }

            // Direction indicator
            var directionIconDrawAction = GenerateDirectionIconDrawAction(lastDrawEndPos,
                rotationFromUpward, Plugin.Config.ScreenMarkSizeScale, 
                IconManager.DirectionScreenIndicatorIconColour, out lastDrawEndPos);
            // Marker
            var markerIconDrawAction = GenerateScreenMarkerIconDrawAction(icon, lastDrawEndPos,
                iconSizeRaw, Plugin.Config.ScreenMarkSizeScale, iconAlpha, out lastDrawEndPos);
            // Altitude diff
            var altDiffIconDrawAction = markerIconDrawAction == null ? null
                : GenerateAltitudeDiffIconDrawAction(obj.AltitudeDiff, lastDrawEndPos, 
                    Plugin.Config.ScreenMarkSizeScale, iconAlpha, out _);
            // Extra info
            var extraInfoDrawAction = GenerateExtraInfoDrawAction(info, Plugin.Config.ScreenMarkSizeScale,
                infoTextColour, textShadowLightness, lastDrawEndPos, iconSizeRaw, rotationFromUpward, out _);
            return DrawAction.Combine(important, directionIconDrawAction, markerIconDrawAction, altDiffIconDrawAction, extraInfoDrawAction);
        }

        protected static DrawAction? GenerateDirectionIconDrawAction(Vector2 drawPos, 
            float rotationFromUpward, float scale, uint colour, out Vector2 drawEndPos)
        {
            var icon = IconManager.DirectionScreenIndicatorIcon;
            var iconHalfSize = IconManager.DirectionScreenIndicatorIconSize * scale / 2;
            (var p1, var p2, var p3, var p4) = UiHelper.GetRotatedRectPointsOnScreen(
                drawPos, iconHalfSize, rotationFromUpward);
            //var iconCentre = (p1 + p3) / 2;
            drawEndPos = new Vector2(drawPos.X + iconHalfSize.X * MathF.Sin(rotationFromUpward),
                drawPos.Y + iconHalfSize.Y * MathF.Cos(rotationFromUpward));
            return icon == null ? null
                : new(() => ImGui.GetWindowDrawList().AddImageQuad(icon.ImGuiHandle,
                    p1, p2, p3, p4, new(0, 0), new(1, 0), new(1, 1), new(0, 1), colour));
        }

        protected static DrawAction? GenerateScreenMarkerIconDrawAction(
            ImGuiScene.TextureWrap? icon, Vector2 screenPosRaw, Vector2 iconSizeRaw, 
            float scale, float alpha, out Vector2 drawEndPos)
        {
            var iconSize = iconSizeRaw * scale;
            drawEndPos = screenPosRaw - iconSize / 2;
            var iconDrawPos = drawEndPos;
            return icon == null ? null 
                : new(() => ImGui.GetWindowDrawList().AddImage(icon.ImGuiHandle, 
                    iconDrawPos, iconDrawPos + iconSize, new(0, 0), new(1, 1), 
                    ImGui.ColorConvertFloat4ToU32(new(1, 1, 1, alpha))));
        }

        protected static DrawAction? GenerateAltitudeDiffIconDrawAction(float altDiff, 
            Vector2 screenPosRaw, float scale, float alpha, out Vector2 drawEndPos)
        {
            drawEndPos = screenPosRaw;
            ImGuiScene.TextureWrap? icon = null;
            if (altDiff > 5) icon = IconManager.AltitudeHigherIcon;
            if (altDiff < -5) icon = IconManager.AltitudeLowerIcon;
            if (icon == null) return null;
            var iconHalfSize = IconManager.AltitudeIconSize * scale / 2;
            return new(() => ImGui.GetWindowDrawList().AddImage(icon.ImGuiHandle,
                screenPosRaw - iconHalfSize, screenPosRaw + iconHalfSize, new(0, 0), new(1, 1), 
                ImGui.ColorConvertFloat4ToU32(new(1, 1, 1, alpha))));
        }

        protected static DrawAction? GenerateExtraInfoDrawAction(string info, float scale,
            Vector4 colour, float shadowLightness, Vector2 markerScreenPos,
            Vector2 markerSizeRaw, float rotationFromUpward, out Vector2 drawEndPos)
        {
            drawEndPos = markerScreenPos;
            if (string.IsNullOrEmpty(info)) return null;
            var fontsize = ImGui.GetFontSize() * scale;
            drawEndPos.Y += 2;  // make it slighly lower
            if (rotationFromUpward > -.2f)
            {
                // direction indicator would be on left side, so just draw text on right
                drawEndPos.X += markerSizeRaw.X * scale + 2;
            }
            else
            {
                // direction indicator would be on right side, so draw text on the left
                var size = UiHelper.GetTextSize(info, ImGui.GetFont(), fontsize);
                drawEndPos.X -= size.X + 2;
            }
            var textDrawPos = drawEndPos;
            return new(() => UiHelper.DrawTextWithShadow(ImGui.GetWindowDrawList(), info, 
                textDrawPos, ImGui.GetFont(), ImGui.GetFontSize(), scale, colour, shadowLightness));
        }

        private protected static Vector2 PushToSideOnXIfNeeded(Vector2 drawPos, bool posInFrontOfCamera)
        {
            if (!posInFrontOfCamera && UiHelper.IsScreenPosInsideMainViewport(drawPos))
            {
                var viewport = ImGui.GetMainViewport();
                // Fix X-axis for some objs: push all those not in front of camera to side
                //  so that they don't dangle in the middle of the screen
                drawPos.X = drawPos.X - UiHelper.GetScreenCentre().X > 0
                    ? (viewport.Pos.X + viewport.Size.X) : viewport.Pos.X;
            }
            return drawPos;
        }

        #endregion
    }
}
