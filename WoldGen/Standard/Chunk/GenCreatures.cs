﻿using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.ServerMods
{
    public class SpawnOppurtunity
    {
        public EntityType ForType;
        public Vec3d Pos;
    }

    public class GenCreatures : ModStdWorldGen
    {
        ICoreServerAPI api;
        Random rnd;
        int worldheight;
        IWorldGenBlockAccessor wgenBlockAccessor;
        CollisionTester collisionTester = new CollisionTester();

       
        Dictionary<EntityType, EntityType[]> entityTypeGroups = new Dictionary<EntityType, EntityType[]>();


        public override bool ShouldLoad(EnumAppSide side)
        {
            return side == EnumAppSide.Server;
        }


        public override double ExecuteOrder()
        {
            return 0.1;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;

            api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.PreDone, EnumPlayStyleFlag.SurviveAndAutomate | EnumPlayStyleFlag.SurviveAndBuild | EnumPlayStyleFlag.WildernessSurvival);

            api.Event.GetWorldgenBlockAccessor(OnWorldGenBlockAccessor);
            api.Event.SaveGameLoaded(GameWorldLoaded);

            // Call our loaded method manually if the server is already running (happens when mods are reloaded at runtime)
            if (api.Server.CurrentRunPhase == EnumServerRunPhase.RunGame)
            {
                GameWorldLoaded();
            }
        }

        private void OnWorldGenBlockAccessor(IChunkProviderThread chunkProvider)
        {
            wgenBlockAccessor = chunkProvider.GetBlockAccessor(true);
        }


        private void GameWorldLoaded()
        {
            LoadGlobalConfig(api);
            rnd = new Random(api.WorldManager.Seed - 18722);
            chunksize = api.WorldManager.ChunkSize;
            worldheight = api.WorldManager.MapSizeY;

            Dictionary<AssetLocation, EntityType> entityTypesByCode = new Dictionary<AssetLocation, EntityType>();

            for (int i = 0; i < api.World.EntityTypes.Length; i++)
            {
                entityTypesByCode[api.World.EntityTypes[i].Code] = api.World.EntityTypes[i];
            }

            for (int i = 0; i < api.World.EntityTypes.Length; i++)
            {
                if (api.World.EntityTypes[i].Server?.SpawnConditions?.Worldgen == null) continue;

                List<EntityType> grouptypes = new List<EntityType>();

                EntityType type = api.World.EntityTypes[i];
                grouptypes.Add(type);

                AssetLocation[] companions = type.Server.SpawnConditions.Worldgen.Companions;
                if (companions == null) continue;

                for (int j = 0; j < companions.Length; j++)
                {
                    EntityType cptype = null;
                    if (entityTypesByCode.TryGetValue(companions[j], out cptype))
                    {
                        grouptypes.Add(cptype);
                    }
                }

                entityTypeGroups[type] = grouptypes.ToArray();
            }
        }


        int climateUpLeft;
        int climateUpRight;
        int climateBotLeft;
        int climateBotRight;


        private void OnChunkColumnGen(IServerChunk[] chunks, int chunkX, int chunkZ)
        {
            IntMap climateMap = chunks[0].MapChunk.MapRegion.ClimateMap;
            ushort[] heightMap = chunks[0].MapChunk.WorldGenTerrainHeightMap;

            int regionChunkSize = api.WorldManager.RegionSize / chunksize;
            int rlX = chunkX % regionChunkSize;
            int rlZ = chunkZ % regionChunkSize;

            float facC = (float)climateMap.InnerSize / regionChunkSize;
            climateUpLeft = climateMap.GetUnpaddedInt((int)(rlX * facC), (int)(rlZ * facC));
            climateUpRight = climateMap.GetUnpaddedInt((int)(rlX * facC + facC), (int)(rlZ * facC));
            climateBotLeft = climateMap.GetUnpaddedInt((int)(rlX * facC), (int)(rlZ * facC + facC));
            climateBotRight = climateMap.GetUnpaddedInt((int)(rlX * facC + facC), (int)(rlZ * facC + facC));

            Vec3d posAsVec = new Vec3d();
            BlockPos pos = new BlockPos();

            foreach (var val in entityTypeGroups)
            {
                EntityType entitytype = val.Key;
                float tries = entitytype.Server.SpawnConditions.Worldgen.TriesPerChunk.nextFloat(1, rnd);

                while (tries-- > rnd.NextDouble())
                {
                    int dx = rnd.Next(chunksize);
                    int dz = rnd.Next(chunksize);

                    pos.Set(chunkX * chunksize + dx, 0, chunkZ * chunksize + dz);

                    pos.Y = 
                        entitytype.Server.SpawnConditions.Worldgen.TryOnlySurface ? 
                        heightMap[dz * chunksize + dx] + 1: 
                        rnd.Next(worldheight)
                    ;
                    posAsVec.Set(pos.X + 0.5, pos.Y + 0.005, pos.Z + 0.5);

                    TrySpawnGroupAt(pos, posAsVec, entitytype, val.Value);
                }
            }
        }


        List<SpawnOppurtunity> spawnPositions = new List<SpawnOppurtunity>();

        private void TrySpawnGroupAt(BlockPos origin, Vec3d posAsVec, EntityType entityType, EntityType[] grouptypes)
        {
            BlockPos pos = origin.Copy();

            int climate = GameMath.BiLerpRgbColor((float)(posAsVec.X % chunksize) / chunksize, (float)(posAsVec.Z % chunksize) / chunksize, climateUpLeft, climateUpRight, climateBotLeft, climateBotRight);
            float temp = TerraGenConfig.GetScaledAdjustedTemperatureFloat((climate >> 16) & 0xff, (int)posAsVec.Y - TerraGenConfig.seaLevel);
            float rain = ((climate >> 8) & 0xff) / 255f;
            int spawned = 0;

            WorldGenSpawnConditions sc = entityType.Server.SpawnConditions.Worldgen;
            bool hasCompanions = sc.Companions != null && sc.Companions.Length > 0;

            spawnPositions.Clear();

            int nextGroupSize = 0;
            int tries = 10;
            while (nextGroupSize <= 0 && tries-- > 0)
            {
                float val = sc.GroupSize.nextFloat();
                nextGroupSize = (int)val + ((val - (int)val) > rnd.NextDouble() ? 1 : 0);
            }

            
            for (int i = 0; i < nextGroupSize*4 + 5; i++)
            {
                if (spawned >= nextGroupSize) break;

                EntityType typeToSpawn = entityType;

                // First entity 80% chance to spawn the dominant creature, every subsequent only 20% chance for males (or even lower if more than 5 companion types)
                double dominantChance = i == 0 ? 0.8 : Math.Min(0.2, 1f / grouptypes.Length);

                if (grouptypes.Length > 1 && rnd.NextDouble() > dominantChance)
                {
                    typeToSpawn = grouptypes[1 + rnd.Next(grouptypes.Length - 1)];
                }

                posAsVec.Set(pos.X + 0.5, pos.Y + 0.005, pos.Z + 0.5);

                IBlockAccessor blockAccesssor = wgenBlockAccessor.GetChunkAtBlockPos(pos.X, pos.Y, pos.Z) == null ? api.World.BlockAccessor : wgenBlockAccessor;

                IMapChunk mapchunk = blockAccesssor.GetMapChunkAtBlockPos(pos);
                if (mapchunk != null)
                {
                    ushort[] heightMap = mapchunk.WorldGenTerrainHeightMap;

                    pos.Y =
                        sc.TryOnlySurface ?
                        heightMap[((int)pos.Z % chunksize) * chunksize + ((int)pos.X % chunksize)] + 1 :
                        pos.Y
                    ;

                    climate = GameMath.BiLerpRgbColor((float)(posAsVec.X % chunksize) / chunksize, (float)(posAsVec.Z % chunksize) / chunksize, climateUpLeft, climateUpRight, climateBotLeft, climateBotRight);
                    temp = TerraGenConfig.GetScaledAdjustedTemperatureFloat((climate >> 16) & 0xff, (int)posAsVec.Y - TerraGenConfig.seaLevel);
                    rain = ((climate >> 8) & 0xff) / 255f;



                    if (CanSpawnAt(blockAccesssor, typeToSpawn, pos, posAsVec, sc, rain, temp))
                    {
                        spawnPositions.Add(new SpawnOppurtunity() { ForType = typeToSpawn, Pos = posAsVec.Clone() });
                        spawned++;
                    }
                }

                pos.X = origin.X + ((rnd.Next(11) - 5) + (rnd.Next(11) - 5)) / 2;
                pos.Z = origin.Z + ((rnd.Next(11) - 5) + (rnd.Next(11) - 5)) / 2;
            }


            // Only spawn if the group reached the minimum group size
            if (spawnPositions.Count >= nextGroupSize)
            {
                long herdId = api.WorldManager.GetNextHerdId();

                foreach (SpawnOppurtunity so in spawnPositions)
                {
                    Entity ent = CreateEntity(so.ForType, so.Pos);
                    if (ent is EntityAgent)
                    {
                        (ent as EntityAgent).HerdId = herdId;
                    }

                    if (wgenBlockAccessor.GetChunkAtBlockPos(pos.X, pos.Y, pos.Z) == null)
                    {
                        api.World.SpawnEntity(ent);
                    }
                    else
                    {
                        wgenBlockAccessor.AddEntity(ent);
                    }
                }

                //Console.WriteLine("Spawn a group of {0}x{1} at {2}", spawnPositions.Count, entityType.Code, origin);
            }
        }


        private Entity CreateEntity(EntityType entityType, Vec3d spawnPosition)
        {
            Entity entity = api.ClassRegistry.CreateEntity(entityType.Class);
            entity.SetType(entityType);
            entity.ServerPos.SetPos(spawnPosition);
            entity.ServerPos.SetYaw(rnd.Next() * GameMath.TWOPI);
            entity.Pos.SetFrom(entity.ServerPos);
            return entity;
        }





        private bool CanSpawnAt(IBlockAccessor blockAccessor, EntityType type, BlockPos pos, Vec3d posAsVec, BaseSpawnConditions sc, float rain, float temp)
        {
            if (!api.World.BlockAccessor.IsValidPos(pos)) return false;

            float? lightLevel = blockAccessor.GetLightLevel(pos, EnumLightLevelType.MaxLight);

            if (lightLevel == null) return false;
            if (sc.MinLightLevel > lightLevel || sc.MaxLightLevel < lightLevel) return false;
            if (sc.MinTemp > temp || sc.MaxTemp < temp) return false;
            if (sc.MinRain > rain || sc.MaxRain < rain) return false;

            Block belowBlock = blockAccessor.GetBlock(pos.X, pos.Y - 1, pos.Z);
            if (!belowBlock.CanCreatureSpawnOn(blockAccessor, pos.DownCopy(), type, sc))
            { 
                return false;
            }

            Block block = blockAccessor.GetBlock(pos);
            if (!block.WildCardMatch(sc.InsideBlockCodes)) return false;

            Cuboidf collisionBox = new Cuboidf()
            {
                X1 = -type.HitBoxSize.X / 2,
                Z1 = -type.HitBoxSize.X / 2,
                X2 = type.HitBoxSize.X / 2,
                Z2 = type.HitBoxSize.X / 2,
                Y2 = type.HitBoxSize.Y
            }.OmniNotDownGrowBy(0.1f);

            return !IsColliding(collisionBox, posAsVec);
        }




        // Custom implementation for mixed generating/loaded chunk access, since we can spawn entities just fine in either loaded or still generating chunks
        public bool IsColliding(Cuboidf entityBoxRel, Vec3d pos)
        {
            BlockPos blockPos = new BlockPos();
            IBlockAccessor blockAccess;
            int chunksize = wgenBlockAccessor.ChunkSize;

            Cuboidd entityCuboid = entityBoxRel.ToDouble().Translate(pos);
            Vec3d blockPosAsVec = new Vec3d();

            int minX = (int)(entityBoxRel.X1 + pos.X);
            int minY = (int)(entityBoxRel.Y1 + pos.Y);
            int minZ = (int)(entityBoxRel.Z1 + pos.Z);
            int maxX = (int)Math.Ceiling(entityBoxRel.X2 + pos.X);
            int maxY = (int)Math.Ceiling(entityBoxRel.Y2 + pos.Y);
            int maxZ = (int)Math.Ceiling(entityBoxRel.Z2 + pos.Z);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        blockAccess = wgenBlockAccessor;
                        IWorldChunk chunk = wgenBlockAccessor.GetChunkAtBlockPos(x, y, z);
                        if (chunk == null)
                        {
                            chunk = api.World.BlockAccessor.GetChunkAtBlockPos(x, y, z);
                            blockAccess = api.World.BlockAccessor;
                        }
                        if (chunk == null) return true;

                        chunk.Unpack();

                        int index = ((y % chunksize) * chunksize + (z % chunksize)) * chunksize + (x % chunksize);
                        Block block = api.World.Blocks[chunk.Blocks[index]];

                        blockPos.Set(x, y, z);
                        blockPosAsVec.Set(x, y, z);

                        Cuboidf[] collisionBoxes = block.GetCollisionBoxes(blockAccess, blockPos);
                        for (int i = 0; collisionBoxes != null && i < collisionBoxes.Length; i++)
                        {
                            Cuboidf collBox = collisionBoxes[i];
                            if (collBox != null && entityCuboid.Intersects(collBox, blockPosAsVec)) return true;
                        }
                    }
                }
            }
            
            return false;
        }
    }
}
