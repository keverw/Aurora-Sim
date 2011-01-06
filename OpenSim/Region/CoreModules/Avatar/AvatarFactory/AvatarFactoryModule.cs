/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

using System.Threading;
using System.Timers;
using System.Collections.Generic;

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory
{
    public class AvatarFactoryModule : IAvatarFactory, INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private Scene m_scene = null;

        private int m_savetime = 5; // seconds to wait before saving changed appearance
        private int m_sendtime = 2; // seconds to wait before sending changed appearance

        private int m_checkTime = 500; // milliseconds to wait between checks for appearance updates
        private System.Timers.Timer m_updateTimer = new System.Timers.Timer();
        private Dictionary<UUID, long> m_savequeue = new Dictionary<UUID, long>();
        private Dictionary<UUID, long> m_sendqueue = new Dictionary<UUID, long>();

        private object m_setAppearanceLock = new object();

        #region INonSharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            if (config != null)
            {
                IConfig sconfig = config.Configs["Startup"];
                if (sconfig != null)
                {
                    m_savetime = sconfig.GetInt("DelayBeforeAppearanceSave", m_savetime);
                    m_sendtime = sconfig.GetInt("DelayBeforeAppearanceSend", m_sendtime);
                    // m_log.InfoFormat("[AVFACTORY] configured for {0} save and {1} send",m_savetime,m_sendtime);
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (m_scene == null)
                m_scene = scene;

            scene.RegisterModuleInterface<IAvatarFactory>(this);
            scene.EventManager.OnNewClient += NewClient;
            scene.EventManager.OnClosingClient += RemoveClient;

            m_updateTimer.Enabled = false;
            m_updateTimer.AutoReset = true;
            m_updateTimer.Interval = m_checkTime; // 500 milliseconds wait to start async ops
            m_updateTimer.Elapsed += HandleAppearanceUpdateTimer;
        }

        public void RemoveRegion(Scene scene)
        {
            scene.UnregisterModuleInterface<IAvatarFactory>(this);
            scene.EventManager.OnNewClient -= NewClient;
            scene.EventManager.OnClosingClient -= RemoveClient;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "Default Avatar Factory"; }
        }

        public void NewClient(IClientAPI client)
        {
            client.OnRequestWearables += SendWearables;
            client.OnSetAppearance += SetAppearance;
            client.OnAvatarNowWearing += AvatarIsWearing;
            client.OnAgentCachedTextureRequest += AgentCachedTexturesRequest;
        }

        public void RemoveClient(IClientAPI client)
        {
            client.OnRequestWearables -= SendWearables;
            client.OnSetAppearance -= SetAppearance;
            client.OnAvatarNowWearing -= AvatarIsWearing;
            client.OnAgentCachedTextureRequest -= AgentCachedTexturesRequest;
        }

        #endregion

        /// <summary>
        /// Check for the existence of the baked texture assets.
        /// </summary>
        /// <param name="client"></param>
        public bool ValidateBakedTextureCache(IClientAPI client)
        {
            return ValidateBakedTextureCache(client, true);
        }

        /// <summary>
        /// Check for the existence of the baked texture assets. Request a rebake
        /// unless checkonly is true.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="checkonly"></param>
        private bool ValidateBakedTextureCache(IClientAPI client, bool checkonly)
        {
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null)
            {
                m_log.WarnFormat("[AvatarFactory]: SetAppearance unable to find presence for {0}", client.AgentId);
                return false;
            }

            bool defonly = true; // are we only using default textures

            // Process the texture entry
            for (int i = 0; i < AvatarAppearance.BAKE_INDICES.Length; i++)
            {
                int idx = AvatarAppearance.BAKE_INDICES[i];
                Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[idx];

                // if there is no texture entry, skip it
                if (face == null)
                    continue;

                // if the texture is one of the "defaults" then skip it
                // this should probably be more intelligent (skirt texture doesnt matter
                // if the avatar isnt wearing a skirt) but if any of the main baked 
                // textures is default then the rest should be as well
                if (face.TextureID == UUID.Zero || face.TextureID == AppearanceManager.DEFAULT_AVATAR_TEXTURE)
                    continue;

                defonly = false; // found a non-default texture reference

                if (!CheckBakedTextureAsset(client, face.TextureID, idx))
                {
                    // the asset didn't exist if we are only checking, then we found a bad
                    // one and we're done otherwise, ask for a rebake
                    if (checkonly) return false;

                    m_log.InfoFormat("[AvatarFactory] missing baked texture {0}, request rebake", face.TextureID);
                    client.SendRebakeAvatarTextures(face.TextureID);
                }
            }

            m_log.DebugFormat("[AvatarFactory]: complete texture check for {0}", client.AgentId);

            // If we only found default textures, then the appearance is not cached
            return (defonly ? false : true);
        }

        /// <summary>
        /// Set appearance data (textureentry and slider settings) received from the client
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="visualParam"></param>
        public void SetAppearance(IClientAPI client, Primitive.TextureEntry textureEntry, byte[] visualParams, WearableCache[] wearables)
        {
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null)
            {
                m_log.WarnFormat("[AvatarFactory]: SetAppearance unable to find presence for {0}", client.AgentId);
                return;
            }

            //m_log.InfoFormat("[AVFACTORY]: start SetAppearance for {0}", client.AgentId);

            // Process the texture entry transactionally, this doesn't guarantee that Appearance is
            // going to be handled correctly but it does serialize the updates to the appearance
            lock (m_setAppearanceLock)
            {
                bool texturesChanged = false;
                bool visualParamsChanged = false;

                if (textureEntry != null)
                {
                    List<UUID> ChangedTextures = new List<UUID>();
                    texturesChanged = sp.Appearance.SetTextureEntries(textureEntry, out ChangedTextures);

                    // m_log.WarnFormat("[AVFACTORY]: Prepare to check textures for {0}",client.AgentId);

                    //Do this async as it accesses the asset service (could be remote) multiple times
                    Util.FireAndForget(delegate(object o) 
                    {
                        //Validate all the textures now that we've updated
                        ValidateBakedTextureCache(client, false);
                        //The client wants us to cache the baked textures
                        CacheWearableData(sp, textureEntry, wearables); 
                    });

                    // m_log.WarnFormat("[AVFACTORY]: Complete texture check for {0}",client.AgentId);
                }
                // Process the visual params, this may change height as well
                if (visualParams != null)
                {
                    //Now update the visual params and see if they have changed
                    visualParamsChanged = sp.Appearance.SetVisualParams(visualParams);

                    //Fix the height only if the parameters have changed
                    if (visualParamsChanged && sp.Appearance.AvatarHeight > 0)
                        sp.SetHeight(sp.Appearance.AvatarHeight);
                }

                // Process the baked texture array
                if (textureEntry != null)
                {
                    //Check for banned clients here
                    Aurora.Framework.IBanViewersModule module = client.Scene.RequestModuleInterface<Aurora.Framework.IBanViewersModule>();
                    if (module != null)
                        module.CheckForBannedViewer(client, textureEntry);
                }

                // If something changed in the appearance then queue an appearance save
                if (texturesChanged || visualParamsChanged)
                    QueueAppearanceSave(client.AgentId);
            }
            // And always queue up an appearance update to send out
            QueueAppearanceSend(client.AgentId);

            // m_log.WarnFormat("[AVFACTORY]: complete SetAppearance for {0}:\n{1}",client.AgentId,sp.Appearance.ToString());
        }

        /// <summary>
        /// Tell the Avatar Service about these baked textures and items
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="textureEntry"></param>
        /// <param name="wearables"></param>
        private void CacheWearableData(ScenePresence sp, Primitive.TextureEntry textureEntry, WearableCache[] wearables)
        {
            if (textureEntry == null || wearables.Length == 0)
                return;

            AvatarWearable cachedWearable = new AvatarWearable();
            cachedWearable.MaxItems = 0; //Unlimited items
            for (int i = 0; i < wearables.Length; i++)
            {
                WearableCache item = wearables[i];
                if (textureEntry.FaceTextures[item.TextureIndex] != null)
                {
                    cachedWearable.Add(item.CacheID, textureEntry.FaceTextures[item.TextureIndex].TextureID);
                }
            }
            m_scene.AvatarService.CacheWearableData(sp.UUID, cachedWearable);
        }

        /// <summary>
        /// The client wants to know whether we already have baked textures for the given items
        /// </summary>
        /// <param name="client"></param>
        /// <param name="args"></param>
        public void AgentCachedTexturesRequest(IClientAPI client, List<CachedAgentArgs> args)
        {
            List<CachedAgentArgs> resp = new List<CachedAgentArgs>();

            AvatarData ad = m_scene.AvatarService.GetAvatar(client.AgentId);
            //Send all with UUID zero for now so that we don't confuse the client about baked textures...
            foreach (CachedAgentArgs arg in args)
            {
                UUID cachedID = UUID.Zero;
                if (ad.Data.ContainsKey("CachedWearables"))
                {
                    OSDArray array = (OSDArray)OSDParser.DeserializeJson(ad.Data["CachedWearables"]);
                    AvatarWearable wearable = new AvatarWearable();
                    wearable.MaxItems = 0; //Unlimited items
                    wearable.Unpack(array);
                    cachedID = wearable.GetAsset(arg.ID);
                }
                CachedAgentArgs respArgs = new CachedAgentArgs();
                respArgs.ID = cachedID;
                respArgs.TextureIndex = arg.TextureIndex;
                resp.Add(respArgs);
            }

            client.SendAgentCachedTexture(resp);
        }

        /// <summary>
        /// Checks for the existance of a baked texture asset and
        /// requests the viewer rebake if the asset is not found
        /// </summary>
        /// <param name="client"></param>
        /// <param name="textureID"></param>
        /// <param name="idx"></param>
        private bool CheckBakedTextureAsset(IClientAPI client, UUID textureID, int idx)
        {
            if (m_scene.AssetService.Get(textureID.ToString()) == null)
            {
                m_log.WarnFormat("[AvatarFactory]: Missing baked texture {0} ({1}) for avatar {2}",
                                 textureID, idx, client.Name);
                return false;
            }
            return true;
        }

        #region UpdateAppearanceTimer

        /// <summary>
        /// Queue up a request to send appearance, makes it possible to
        /// accumulate changes without sending out each one separately.
        /// </summary>
        public void QueueAppearanceSend(UUID agentid)
        {
            // m_log.WarnFormat("[AVFACTORY]: Queue appearance send for {0}", agentid);

            // 10000 ticks per millisecond, 1000 milliseconds per second
            long timestamp = DateTime.Now.Ticks + Convert.ToInt64(m_sendtime * 1000 * 10000);
            lock (m_sendqueue)
            {
                m_sendqueue[agentid] = timestamp;
                m_updateTimer.Start();
            }
        }

        public void QueueAppearanceSave(UUID agentid)
        {
            // m_log.WarnFormat("[AVFACTORY]: Queue appearance save for {0}", agentid);

            // 10000 ticks per millisecond, 1000 milliseconds per second
            long timestamp = DateTime.Now.Ticks + Convert.ToInt64(m_savetime * 1000 * 10000);
            lock (m_savequeue)
            {
                m_savequeue[agentid] = timestamp;
                m_updateTimer.Start();
            }
        }

        private void HandleAppearanceSend(UUID agentid)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentid);
            if (sp == null)
            {
                m_log.WarnFormat("[AvatarFactory]: Agent {0} no longer in the scene", agentid);
                return;
            }

            // m_log.WarnFormat("[AvatarFactory]: Handle appearance send for {0}", agentid);

            //Send our avatar to ourselves as well
            sp.SendAppearanceToAgent(sp);

            // Send the appearance to everyone in the scene
            sp.SendAppearanceToAllOtherAgents();

            // Send animations back to the avatar as well
            sp.Animator.SendAnimPack();
        }

        private void HandleAppearanceSave(UUID agentid)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentid);
            if (sp == null)
            {
                m_log.WarnFormat("[AvatarFactory]: Agent {0} no longer in the scene", agentid);
                return;
            }

            // m_log.WarnFormat("[AvatarFactory] avatar {0} save appearance",agentid);

            m_scene.AvatarService.SetAppearance(agentid, sp.Appearance);
        }

        private void HandleAppearanceUpdateTimer(object sender, EventArgs ea)
        {
            long now = DateTime.Now.Ticks;

            lock (m_sendqueue)
            {
                Dictionary<UUID, long> sends = new Dictionary<UUID, long>(m_sendqueue);
                foreach (KeyValuePair<UUID, long> kvp in sends)
                {
                    if (kvp.Value < now)
                    {
                        Util.FireAndForget(delegate(object o) { HandleAppearanceSend(kvp.Key); });
                        m_sendqueue.Remove(kvp.Key);
                    }
                }
            }

            lock (m_savequeue)
            {
                Dictionary<UUID, long> saves = new Dictionary<UUID, long>(m_savequeue);
                foreach (KeyValuePair<UUID, long> kvp in saves)
                {
                    if (kvp.Value < now)
                    {
                        Util.FireAndForget(delegate(object o) { HandleAppearanceSave(kvp.Key); });
                        m_savequeue.Remove(kvp.Key);
                    }
                }
            }

            if (m_savequeue.Count == 0 && m_sendqueue.Count == 0)
                m_updateTimer.Stop();
        }

        #endregion

        /// <summary>
        /// Tell the client for this scene presence what items it should be wearing now
        /// </summary>
        public void SendWearables(IClientAPI client)
        {
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null)
            {
                m_log.WarnFormat("[AvatarFactory]: SendWearables unable to find presence for {0}", client.AgentId);
                return;
            }

            //Make sure that the timer doesn't go off in the ScenePresence that this avatar hasn't requested its wearables yet
            sp.m_InitialHasWearablesBeenSent = true;
            m_log.DebugFormat("[AvatarFactory]: Received request for wearables of {0}", sp.Name);
            client.SendWearables(sp.Appearance.Wearables, sp.Appearance.Serial);
        }

        /// <summary>
        /// Update what the avatar is wearing using an item from their inventory.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void AvatarIsWearing(IClientAPI client, AvatarWearingArgs e)
        {
            ScenePresence sp = m_scene.GetScenePresence(client.AgentId);
            if (sp == null)
            {
                m_log.WarnFormat("[AvatarFactory]: AvatarIsWearing unable to find presence for {0}", client.AgentId);
                return;
            }

            m_log.DebugFormat("[AvatarFactory]: AvatarIsWearing called for {0}", client.AgentId);

            // operate on a copy of the appearance so we don't have to lock anything
            AvatarAppearance avatAppearance = new AvatarAppearance(sp.Appearance, false);

            foreach (AvatarWearingArgs.Wearable wear in e.NowWearing)
            {
                if (wear.Type < AvatarWearable.MAX_WEARABLES)
                    avatAppearance.Wearables[wear.Type].Add(wear.ItemID, UUID.Zero);
            }

            avatAppearance.GetAssetsFrom(sp.Appearance);

            // This could take awhile since it needs to pull inventory
            SetAppearanceAssets(sp.UUID, ref avatAppearance);

            // could get fancier with the locks here, but in the spirit of "last write wins"
            // this should work correctly, also, we don't need to send the appearance here
            // since the "iswearing" will trigger a new set of visual param and baked texture changes
            // when those complete, the new appearance will be sent
            sp.Appearance = avatAppearance;
            //Do not save or send! The client loops back and sends a bunch of SetAppearance (handled above) and that takes care of it
        }

        private void SetAppearanceAssets(UUID userID, ref AvatarAppearance appearance)
        {
            IInventoryService invService = m_scene.InventoryService;

            if (invService.GetRootFolder(userID) != null)
            {
                for (int i = 0; i < AvatarWearable.MAX_WEARABLES; i++)
                {
                    for (int j = 0; j < appearance.Wearables[j].Count; j++)
                    {
                        if (appearance.Wearables[i][j].ItemID == UUID.Zero)
                            continue;

                        // Ignore ruth's assets
                        if (appearance.Wearables[i][j].ItemID == AvatarWearable.DefaultWearables[i][j].ItemID)
                        {
                            appearance.Wearables[i].Add(appearance.Wearables[i][j].ItemID, AvatarWearable.DefaultWearables[i][j].AssetID);
                            continue;
                        }

                        InventoryItemBase baseItem = new InventoryItemBase(appearance.Wearables[i][j].ItemID, userID);
                        baseItem = invService.GetItem(baseItem);

                        if (baseItem != null)
                        {
                            appearance.Wearables[i].Add(appearance.Wearables[i][j].ItemID, baseItem.AssetID);
                        }
                        else
                        {
                            m_log.ErrorFormat(
                                "[AvatarFactory]: Can't find inventory item {0} for {1}, setting to default",
                                appearance.Wearables[i][j].ItemID, (WearableType)i);

                            appearance.Wearables[i].RemoveItem(appearance.Wearables[i][j].ItemID);
                            appearance.Wearables[i].Add(AvatarWearable.DefaultWearables[i][j].ItemID, AvatarWearable.DefaultWearables[i][j].AssetID);
                        }
                    }
                }
            }
            else
            {
                appearance.Wearables = AvatarWearable.DefaultWearables;
                m_log.WarnFormat("[AvatarFactory]: user {0} has no inventory, setting appearance to default", userID);
            }
        }
    }
}