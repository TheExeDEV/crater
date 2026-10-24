local ReplicatedStorage = game:GetService("ReplicatedStorage")

local Greeting = require(ReplicatedStorage.Shared.Greeting)

print(Greeting.hello("client"))
