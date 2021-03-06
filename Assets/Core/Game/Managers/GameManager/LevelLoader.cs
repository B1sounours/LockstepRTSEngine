﻿using System;
using UnityEngine;

namespace RTSLockstep
{
    [Serializable]
    public struct SpawnInfo
    {
        [DataCode("Agents")]
        public string AgentCode;
        public int Count;
        [DataCode("AgentControllers")]
        public string ControllerCode;
        public Vector2d Position;
    }

    public class LevelLoader : BehaviourHelper
    {
        [SerializeField]
        private SpawnInfo[] Spawns;
        public bool AutoCommand = true;

        protected override void OnInitialize()
        {
        }

        protected override void OnVisualize()
        {
            //if (Input.GetKeyDown(KeyCode.M))
            //{
            //    LaunchSpawns();
            //}
        }

        protected override void OnGameStart()
        {
            LaunchSpawns();
        }

        //integrate into LSF...
        //void OnEnable()
        //{
        //    //Tell our 'OnLevelFinishedLoading' function to start listening for a scene change as soon as this script is enabled.
        //    SceneManager.sceneLoaded += OnLevelFinishedLoading;
        //}

        //void OnDisable()
        //{
        //    //Tell our 'OnLevelFinishedLoading' function to stop listening for a scene change as soon as this script is disabled. Remember to always have an unsubscription for every delegate you subscribe to!
        //    SceneManager.sceneLoaded -= OnLevelFinishedLoading;
        //}

        //void OnLevelFinishedLoading(Scene scene, LoadSceneMode mode)
        //{
        //    if (ResourceManager.LevelName != null && ResourceManager.LevelName != "")
        //    {
        //        LoadManager.LoadGame(ResourceManager.LevelName);
        //    }
        //    Time.timeScale = 1.0f;
        //    ResourceManager.MenuOpen = false;
        //}

        public void LaunchSpawns()
        {
            foreach(SpawnInfo info in Spawns)
            {
                var controller = AgentControllerHelper.Instance.GetInstanceManager(info.ControllerCode);

                for (int j = 0; j < info.Count; j++)
                {
                    RTSAgent agent = controller.CreateAgent(info.AgentCode, info.Position);
                    if (AutoCommand)
                        Selector.Add(agent);
                }
            }

            if (AutoCommand)
            {
                //Find average of spawn positions
                Vector2d battlePos = Vector2d.zero;
                foreach(SpawnInfo info in Spawns)
                {
                    battlePos += info.Position;
                }
                battlePos /= Spawns.Length;
                Command com = new Command(Data.AbilityDataItem.FindInterfacer<Attack>().ListenInputID);
                com.Add<Vector2d>(battlePos);

                PlayerManager.SendCommand(com);
                Selector.Clear();
            }
        }
    }
}