// 演示数据：星系与跳数来自本机 SDE 快照（Tranquility）。
// 2O-EEW 邻域与路径已用 ESI 航线接口抽样校验。

const SYSTEMS = {
  "2O-EEW": { name: "2O-EEW", security: -0.76, region: "Immensea" },
  "J-QA7I": { name: "J-QA7I", security: -0.48, region: "Immensea" },
  "OP9L-F": { name: "OP9L-F", security: -0.58, region: "Immensea" },
  "W4E-IT": { name: "W4E-IT", security: -0.60, region: "Immensea" },
  "Y-N4EF": { name: "Y-N4EF", security: -0.79, region: "Immensea" },
  "7YSF-E": { name: "7YSF-E", security: -0.38, region: "Immensea" },
  "U79-JF": { name: "U79-JF", security: -0.56, region: "Immensea" },
  "Y-FZ5N": { name: "Y-FZ5N", security: -0.41, region: "Immensea" },
  "9-XN3F": { name: "9-XN3F", security: -0.35, region: "Immensea" },
  "ZMV9-A": { name: "ZMV9-A", security: -0.23, region: "Tenerifis" },
  "AC-7LZ": { name: "AC-7LZ", security: -0.30, region: "Immensea" },
  "B-KDOZ": { name: "B-KDOZ", security: -0.25, region: "Immensea" },
  "FE-6YQ": { name: "FE-6YQ", security: -0.20, region: "Tenerifis" },
  "S-9RCJ": { name: "S-9RCJ", security: -0.30, region: "Tenerifis" },
};

const NEIGHBORHOOD = {
  columns: [
    { depth: 0, nodes: ["2O-EEW"] },
    { depth: 1, nodes: ["J-QA7I", "OP9L-F", "W4E-IT", "Y-N4EF"] },
    { depth: 2, nodes: ["7YSF-E", "U79-JF"] },
    { depth: 3, nodes: ["Y-FZ5N"] },
    { depth: 4, nodes: ["9-XN3F", "ZMV9-A"] },
    { depth: 5, nodes: ["AC-7LZ", "B-KDOZ", "FE-6YQ", "S-9RCJ"] },
  ],
  edges: [
    ["2O-EEW", "J-QA7I"], ["2O-EEW", "OP9L-F"], ["2O-EEW", "W4E-IT"], ["2O-EEW", "Y-N4EF"],
    ["J-QA7I", "OP9L-F"], ["J-QA7I", "7YSF-E"],
    ["OP9L-F", "W4E-IT"], ["OP9L-F", "Y-N4EF"],
    ["W4E-IT", "U79-JF"],
    ["U79-JF", "Y-FZ5N"],
    ["Y-FZ5N", "9-XN3F"], ["Y-FZ5N", "ZMV9-A"],
    ["9-XN3F", "AC-7LZ"], ["9-XN3F", "B-KDOZ"],
    ["ZMV9-A", "FE-6YQ"], ["ZMV9-A", "S-9RCJ"],
  ],
};

const SCENARIOS = {
  same: {
    key: "same",
    jumps: 0,
    route: ["2O-EEW"],
    rule: "0-0 跳",
    sound: "警报长鸣",
    volume: 80,
    channel: "fareast.imperium",
    character: "TieTouWa A",
    time: "22:14:03",
  },
  jump3: {
    key: "jump3",
    jumps: 3,
    route: ["2O-EEW", "W4E-IT", "U79-JF", "Y-FZ5N"],
    rule: "3-5 跳",
    sound: "低沉蜂鸣",
    volume: 80,
    channel: "fareast.imperium",
    character: "TieTouWa A",
    time: "22:14:03",
    alternatives: [
      { jumps: 4, route: ["2O-EEW", "OP9L-F", "W4E-IT", "U79-JF", "Y-FZ5N"] },
    ],
  },
  jump5: {
    key: "jump5",
    jumps: 5,
    route: ["2O-EEW", "W4E-IT", "U79-JF", "Y-FZ5N", "ZMV9-A", "S-9RCJ"],
    rule: "3-5 跳",
    sound: "低沉蜂鸣",
    volume: 80,
    channel: "fareast.imperium",
    character: "TieTouWa A",
    time: "22:14:03",
  },
  batch: {
    key: "batch",
    batchSound: "短促嘀声",
    batchRule: "1-2 跳",
    batch: [
      {
        jumps: 1,
        route: ["S-9RCJ", "ZMV9-A"],
        rule: "1-2 跳",
        sound: "短促嘀声",
        volume: 80,
        channel: "fareast.imperium",
        character: "String C",
        time: "22:14:05",
      },
      {
        jumps: 2,
        route: ["W4E-IT", "U79-JF", "Y-FZ5N"],
        rule: "1-2 跳",
        sound: "短促嘀声",
        volume: 80,
        channel: "fareast.imperium",
        character: "String B",
        time: "22:14:04",
      },
      {
        jumps: 3,
        route: ["2O-EEW", "W4E-IT", "U79-JF", "Y-FZ5N"],
        rule: "3-5 跳",
        sound: "低沉蜂鸣",
        volume: 80,
        channel: "new.chinese",
        character: "TieTouWa A",
        time: "22:14:03",
        alternatives: [
          { jumps: 4, route: ["2O-EEW", "OP9L-F", "W4E-IT", "U79-JF", "Y-FZ5N"] },
        ],
      },
    ],
  },
};

const CURRENT_SYSTEMS = {
  "TieTouWa A": "2O-EEW",
  "String B": "W4E-IT",
  "String C": "S-9RCJ",
  "String D": "位置未知",
};

const CHARACTERS = [
  {
    id: 2122365291,
    name: "TieTouWa A",
    channels: [
      { name: "fareast.imperium", id: "player_51799e615e6811f0ada800109bd0f828", type: "玩家频道", last: "22:10" },
      { name: "新中文", id: "player_c97466a1c79811ef88e500109bd0fa48", type: "玩家频道", last: "22:08" },
      { name: "舰队", id: "fleet_1021112315498", type: "舰队频道", last: "22:10", dynamic: true },
      { name: "本地", id: "local", type: "本地频道", last: "22:10" },
    ],
  },
  {
    id: 2117003909,
    name: "String B",
    channels: [
      { name: "fareast.imperium", id: "player_51799e615e6811f0ada800109bd0f828", type: "玩家频道", last: "22:10" },
      { name: "isk.imperium", id: "player_4f2d6b3a77e411efa1b900109bd0fa48", type: "玩家频道", last: "21:58" },
      { name: "本地", id: "local", type: "本地频道", last: "22:10" },
    ],
  },
  {
    id: 2122179223,
    name: "String C",
    channels: [
      { name: "fareast.imperium", id: "player_51799e615e6811f0ada800109bd0f828", type: "玩家频道", last: "22:10" },
      { name: "舰队", id: "fleet_1021112315498", type: "舰队频道", last: "22:10", dynamic: true },
      { name: "本地", id: "local", type: "本地频道", last: "22:10" },
    ],
  },
  {
    id: 2122723342,
    name: "String D",
    channels: [
      { name: "east.imperium", id: "player_9a1c5e2d88a211efa0c500109bd0fa48", type: "玩家频道", last: "21:44" },
      { name: "本地", id: "local", type: "本地频道", last: "21:44" },
    ],
  },
];

const LISTENERS = [
  {
    id: "l1",
    characterId: 2122365291,
    character: "TieTouWa A",
    channel: "fareast.imperium",
    channelId: "player_51799e615e6811f0ada800109bd0f828",
    type: "玩家频道",
    system: "2O-EEW",
    status: "监听中",
    enabled: true,
  },
  {
    id: "l2",
    characterId: 2122365291,
    character: "TieTouWa A",
    channel: "新中文",
    channelId: "player_c97466a1c79811ef88e500109bd0fa48",
    type: "玩家频道",
    system: "2O-EEW",
    status: "监听中",
    enabled: true,
  },
  {
    id: "l3",
    characterId: 2117003909,
    character: "String B",
    channel: "fareast.imperium",
    channelId: "player_51799e615e6811f0ada800109bd0f828",
    type: "玩家频道",
    system: "W4E-IT",
    status: "监听中",
    enabled: true,
  },
  {
    id: "l4",
    characterId: 2122179223,
    character: "String C",
    channel: "舰队",
    channelId: "fleet_1021112315498",
    type: "舰队频道",
    system: "S-9RCJ",
    status: "监听中",
    enabled: true,
  },
  {
    id: "l5",
    characterId: 2122723342,
    character: "String D",
    channel: "AB",
    channelId: "",
    type: "手动输入",
    system: "位置未知",
    status: "等待日志",
    enabled: true,
  },
];

const ALARM_SOUNDS = [
  { id: "alarm", name: "警报长鸣" },
  { id: "beep", name: "短促嘀声" },
  { id: "rising", name: "三段上升" },
  { id: "buzz", name: "低沉蜂鸣" },
  { id: "mech", name: "机械提示" },
];

const RULES = [
  { id: "r0", min: 0, max: 0, sound: "alarm", volume: 80, enabled: true },
  { id: "r1", min: 1, max: 2, sound: "beep", volume: 80, enabled: true },
  { id: "r2", min: 3, max: 5, sound: "buzz", volume: 80, enabled: true },
  { id: "r3", min: 6, max: 10, sound: "mech", volume: 70, enabled: true },
];

window.MOCK = {
  SYSTEMS,
  NEIGHBORHOOD,
  SCENARIOS,
  ALARM_SOUNDS,
  RULES,
  CHARACTERS,
  LISTENERS,
  CURRENT_SYSTEMS,
};
